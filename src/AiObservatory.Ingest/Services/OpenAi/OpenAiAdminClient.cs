using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using NodaTime;

namespace AiObservatory.Ingest.Services.OpenAi;

/// <summary>Reads complete, validated OpenAI organization usage and cost reports.</summary>
public sealed class OpenAiAdminClient(HttpClient http) : IOpenAiAdminClient
{
    private const int MaximumPages = 10_000;
    private const int MaximumResponseBytes = 2 * 1024 * 1024;

    public Task<IReadOnlyList<OpenAiUsageRecord>> GetUsageAsync(
        LocalDate from,
        LocalDate through,
        CancellationToken cancellationToken = default
    ) =>
        GetAllAsync(
            "/v1/organization/usage/completions",
            "&bucket_width=1d&group_by%5B%5D=model&group_by%5B%5D=batch&group_by%5B%5D=service_tier&limit=31",
            from,
            through,
            ParseUsagePage,
            cancellationToken
        );

    public Task<IReadOnlyList<OpenAiCostRecord>> GetCostsAsync(
        LocalDate from,
        LocalDate through,
        CancellationToken cancellationToken = default
    ) =>
        GetAllAsync(
            "/v1/organization/costs",
            "&bucket_width=1d&group_by%5B%5D=project_id&group_by%5B%5D=line_item&limit=180",
            from,
            through,
            ParseCostPage,
            cancellationToken
        );

    private async Task<IReadOnlyList<T>> GetAllAsync<T>(
        string path,
        string query,
        LocalDate from,
        LocalDate through,
        Func<JsonElement, long, long, IReadOnlyList<T>> parsePage,
        CancellationToken cancellationToken
    )
    {
        if (through < from)
        {
            throw new ArgumentException("The end date must not precede the start date.", nameof(through));
        }

        var start = ToUnixSeconds(from);
        var end = ToUnixSeconds(through.PlusDays(1));
        var records = new List<T>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;

        for (var page = 0; page < MaximumPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = string.Create(
                CultureInfo.InvariantCulture,
                $"{path}?start_time={start}&end_time={end}{query}{(cursor is null ? string.Empty : $"&page={Uri.EscapeDataString(cursor)}")}"
            );
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken
            );
            response.EnsureSuccessStatusCode();
            var body = await ReadBoundedAsync(response.Content, cancellationToken);

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(body);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("OpenAI returned malformed JSON.", exception);
            }

            using (document)
            {
                var root = RequireObject(document.RootElement, "page");
                foreach (var record in parsePage(root, start, end))
                {
                    records.Add(record);
                }

                var hasMore = RequireBoolean(root, "has_more");
                var next = RequireCursor(root, hasMore);
                if (!hasMore)
                {
                    return Array.AsReadOnly(records.ToArray());
                }
                if (page == MaximumPages - 1)
                {
                    throw new InvalidDataException($"OpenAI pagination exceeded {MaximumPages} pages.");
                }
                if (!cursors.Add(next!))
                {
                    throw new InvalidDataException("OpenAI pagination repeated a cursor.");
                }
                cursor = next;
            }
        }

        throw new InvalidDataException($"OpenAI pagination exceeded {MaximumPages} pages.");
    }

    private static IReadOnlyList<OpenAiUsageRecord> ParseUsagePage(JsonElement root, long from, long throughExclusive)
    {
        var records = new List<OpenAiUsageRecord>();
        foreach (var bucket in RequireArray(root, "data").EnumerateArray())
        {
            ValidateObjectType(bucket, "bucket");
            var (bucketStart, bucketEnd) = ReadBucket(bucket, from, throughExclusive);
            foreach (var result in RequireArray(bucket, "results").EnumerateArray())
            {
                ValidateObjectType(result, "organization.usage.completions.result");
                var model = OptionalNonBlankString(result, "model");
                var batch = OptionalBoolean(result, "batch");
                var serviceTier = OptionalNonBlankString(result, "service_tier");
                var totalInput = RequireNonNegativeInt64(result, "input_tokens");
                var (uncached, cached, cacheWrite) = InputLanes(result, totalInput);
                var output = RequireNonNegativeInt64(result, "output_tokens");
                var requests = RequireNonNegativeInt64(result, "num_model_requests");

                var processing = Processing(batch, serviceTier);
                records.Add(
                    new OpenAiUsageRecord(
                        bucketStart,
                        bucketEnd,
                        model,
                        batch,
                        serviceTier,
                        processing,
                        uncached,
                        cached,
                        cacheWrite,
                        output,
                        requests,
                        RawEvidence(bucket, result, processing)
                    )
                );
            }
        }
        return records;
    }

    // The split lanes are documented as optional: older buckets and non-cache-capable
    // models may omit them. Missing lanes default to zero and the uncached lane is
    // derived from the total. When the uncached lane is declared explicitly the sum
    // invariant applies even to a partial split, with absent lanes counting as zero.
    private static (long Uncached, long Cached, long CacheWrite) InputLanes(JsonElement result, long totalInput)
    {
        var uncachedLane = OptionalNonNegativeInt64(result, "input_uncached_tokens");
        var cachedLane = OptionalNonNegativeInt64(result, "input_cached_tokens");
        var cacheWriteLane = OptionalNonNegativeInt64(result, "input_cache_write_tokens");
        var cached = cachedLane ?? 0;
        var cacheWrite = cacheWriteLane ?? 0;
        if (uncachedLane is { } declaredUncached)
        {
            long splitInput;
            try
            {
                splitInput = checked(declaredUncached + cached + cacheWrite);
            }
            catch (OverflowException exception)
            {
                throw new InvalidDataException("OpenAI input token lanes overflow.", exception);
            }
            if (splitInput != totalInput)
            {
                throw new InvalidDataException("OpenAI input token lanes do not equal input_tokens.");
            }
            return (declaredUncached, cached, cacheWrite);
        }

        long uncached;
        try
        {
            uncached = checked(totalInput - cached - cacheWrite);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("OpenAI input token lanes overflow.", exception);
        }
        if (uncached < 0)
        {
            throw new InvalidDataException("OpenAI input token lanes exceed input_tokens.");
        }
        return (uncached, cached, cacheWrite);
    }

    private static IReadOnlyList<OpenAiCostRecord> ParseCostPage(JsonElement root, long from, long throughExclusive)
    {
        var records = new List<OpenAiCostRecord>();
        foreach (var bucket in RequireArray(root, "data").EnumerateArray())
        {
            ValidateObjectType(bucket, "bucket");
            var (bucketStart, bucketEnd) = ReadBucket(bucket, from, throughExclusive);
            foreach (var result in RequireArray(bucket, "results").EnumerateArray())
            {
                records.Add(ParseCostResult(bucketStart, bucketEnd, bucket, result));
            }
        }
        return records;
    }

    private static OpenAiCostRecord ParseCostResult(
        Instant bucketStart,
        Instant bucketEnd,
        JsonElement bucket,
        JsonElement result
    )
    {
        ValidateObjectType(result, "organization.costs.result");
        if (!result.TryGetProperty("amount", out var amountElement) || amountElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("OpenAI cost result is missing amount.");
        }
        var amount = RequireDecimal(amountElement, "value");
        if (amount < 0m)
        {
            throw new InvalidDataException("OpenAI cost amount cannot be negative.");
        }
        var currency = RequireNonBlankString(amountElement, "currency");
        if (!string.Equals(currency, "usd", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("OpenAI cost currency is unsupported.");
        }
        var quantity = OptionalDecimal(result, "quantity");
        if (quantity < 0m)
        {
            throw new InvalidDataException("OpenAI cost quantity cannot be negative.");
        }

        return new OpenAiCostRecord(
            bucketStart,
            bucketEnd,
            amount,
            "USD",
            OptionalNonBlankString(result, "line_item"),
            OptionalNonBlankString(result, "project_id"),
            quantity,
            OptionalNonBlankString(result, "quantity_unit"),
            RawEvidence(bucket, result, processing: null)
        );
    }

    private static (Instant Start, Instant End) ReadBucket(JsonElement bucket, long from, long throughExclusive)
    {
        var start = RequireInt64(bucket, "start_time");
        var end = RequireInt64(bucket, "end_time");
        if (start < from || end > throughExclusive || end - start != 86_400)
        {
            throw new InvalidDataException("OpenAI returned a bucket outside the requested daily range.");
        }

        try
        {
            return (Instant.FromUnixTimeSeconds(start), Instant.FromUnixTimeSeconds(end));
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException("OpenAI returned an invalid bucket timestamp.", exception);
        }
    }

    private static string RawEvidence(JsonElement bucket, JsonElement result, string? processing)
    {
        // The bucket's "results" array holds every sibling result; embedding it per record
        // made payloads O(N^2). Keep the bucket's own fields and this result only.
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("processing");
            if (processing is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStringValue(processing);
            }
            writer.WritePropertyName("bucket");
            writer.WriteStartObject();
#pragma warning disable S3267 // Element-ordered writer side effects don't read as a LINQ Where.
            foreach (var property in bucket.EnumerateObject())
            {
                if (!property.NameEquals("results"))
                {
                    property.WriteTo(writer);
                }
            }
#pragma warning restore S3267
            writer.WriteEndObject();
            writer.WritePropertyName("result");
            result.WriteTo(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }

    private static string? Processing(bool? batch, string? serviceTier)
    {
        if (batch == true)
        {
            return "batch";
        }
        return serviceTier switch
        {
            "default" => "standard",
            "flex" => "flex",
            "priority" => "fast",
            _ => null,
        };
    }

    private static long ToUnixSeconds(LocalDate date) =>
        date.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant().ToUnixTimeSeconds();

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes)
        {
            throw new InvalidDataException("OpenAI response exceeds the size limit.");
        }

        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    return destination.ToArray();
                }
                if (destination.Length + read > MaximumResponseBytes)
                {
                    throw new InvalidDataException("OpenAI response exceeds the size limit.");
                }
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static JsonElement RequireObject(JsonElement element, string objectType)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("OpenAI page must be a JSON object.");
        }
        ValidateObjectType(element, objectType);
        return element;
    }

    private static void ValidateObjectType(JsonElement element, string expected)
    {
        if (element.ValueKind != JsonValueKind.Object || RequireNonBlankString(element, "object") != expected)
        {
            throw new InvalidDataException($"OpenAI returned an unexpected object type; expected {expected}.");
        }
    }

    private static JsonElement RequireArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"OpenAI response is missing {propertyName}.");
        }
        return value;
    }

    private static bool RequireBoolean(JsonElement element, string propertyName)
    {
        if (
            !element.TryGetProperty(propertyName, out var value)
            || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
        )
        {
            throw new InvalidDataException($"OpenAI response is missing {propertyName}.");
        }
        return value.GetBoolean();
    }

    private static bool? OptionalBoolean(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException($"OpenAI {propertyName} must be boolean or null.");
        }
        return value.GetBoolean();
    }

    private static string? RequireCursor(JsonElement root, bool hasMore)
    {
        if (!root.TryGetProperty("next_page", out var value))
        {
            throw new InvalidDataException("OpenAI response is missing next_page.");
        }
        if (!hasMore)
        {
            if (value.ValueKind != JsonValueKind.Null)
            {
                throw new InvalidDataException("OpenAI final page has an unexpected cursor.");
            }
            return null;
        }
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException("OpenAI response requires a non-empty next_page cursor.");
        }
        return value.GetString();
    }

    private static string RequireNonBlankString(JsonElement element, string propertyName)
    {
        if (
            !element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString())
        )
        {
            throw new InvalidDataException($"OpenAI response is missing {propertyName}.");
        }
        return value.GetString()!;
    }

    private static string? OptionalNonBlankString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"OpenAI {propertyName} must be a non-empty string or null.");
        }
        return value.GetString();
    }

    private static long? OptionalNonNegativeInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        // TryGetInt64 throws InvalidOperationException (not the documented InvalidDataException)
        // for a non-number kind, so a string like "400" must be rejected on ValueKind first.
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var parsed) || parsed < 0)
        {
            throw new InvalidDataException($"OpenAI {propertyName} must be a non-negative integer or null.");
        }
        return parsed;
    }

    private static long RequireNonNegativeInt64(JsonElement element, string propertyName)
    {
        var value = RequireInt64(element, propertyName);
        if (value < 0)
        {
            throw new InvalidDataException($"OpenAI {propertyName} cannot be negative.");
        }
        return value;
    }

    private static long RequireInt64(JsonElement element, string propertyName)
    {
        // The ValueKind guard comes first: TryGetInt64 throws InvalidOperationException for a
        // non-number kind, bypassing the documented InvalidDataException contract.
        if (
            !element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var parsed)
        )
        {
            throw new InvalidDataException($"OpenAI response is missing {propertyName}.");
        }
        return parsed;
    }

    private static decimal RequireDecimal(JsonElement element, string propertyName)
    {
        // The ValueKind guard comes first, exactly as in RequireInt64: TryGetDecimal throws
        // InvalidOperationException for a non-number kind, bypassing the documented
        // InvalidDataException contract.
        if (
            !element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDecimal(out var parsed)
        )
        {
            throw new InvalidDataException($"OpenAI response is missing {propertyName}.");
        }
        return parsed;
    }

    private static decimal? OptionalDecimal(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        // Same ValueKind guard as RequireDecimal: a string like "1.5" makes TryGetDecimal
        // throw InvalidOperationException instead of the documented InvalidDataException.
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var parsed))
        {
            throw new InvalidDataException($"OpenAI {propertyName} must be numeric or null.");
        }
        return parsed;
    }
}
