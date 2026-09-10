using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AiObservatory.Ingest.Sources;
using NodaTime;
using NodaTime.Text;

namespace AiObservatory.Ingest.Services.Anthropic;

/// <summary>Reads complete, validated Anthropic Platform Admin reports.</summary>
public sealed class AnthropicAdminClient(HttpClient http) : IAnthropicAdminClient
{
    private const int MaximumPages = 10_000;
    private const int MaximumResponseBytes = 2 * 1024 * 1024;

    public Task<IReadOnlyList<AnthropicUsageRecord>> GetMessageUsageAsync(
        LocalDate from,
        LocalDate through,
        CancellationToken cancellationToken = default
    )
    {
        ValidateRange(from, through);
        var rangeStart = StartOfDay(from);
        var rangeEnd = StartOfDay(through.PlusDays(1));
        return GetAllPagesAsync(
            cursor =>
                BuildRangeUrl(
                    "/v1/organizations/usage_report/messages",
                    rangeStart,
                    rangeEnd,
                    "&bucket_width=1d&group_by%5B%5D=model&group_by%5B%5D=service_tier&group_by%5B%5D=inference_geo&group_by%5B%5D=speed&limit=31",
                    cursor
                ),
            root => ParseMessagePage(root, rangeStart, rangeEnd),
            cancellationToken
        );
    }

    public Task<IReadOnlyList<AnthropicCostRecord>> GetCostsAsync(
        LocalDate from,
        LocalDate through,
        CancellationToken cancellationToken = default
    )
    {
        ValidateRange(from, through);
        var rangeStart = StartOfDay(from);
        var rangeEnd = StartOfDay(through.PlusDays(1));
        return GetAllPagesAsync(
            cursor =>
                BuildRangeUrl(
                    "/v1/organizations/cost_report",
                    rangeStart,
                    rangeEnd,
                    "&bucket_width=1d&group_by%5B%5D=workspace_id&group_by%5B%5D=description&limit=31",
                    cursor
                ),
            root => ParseCostPage(root, rangeStart, rangeEnd),
            cancellationToken
        );
    }

    public async Task<IReadOnlyList<ClaudeCodeUsageRecord>> GetClaudeCodeUsageAsync(
        LocalDate from,
        LocalDate through,
        CancellationToken cancellationToken = default
    )
    {
        ValidateRange(from, through);
        var records = new List<ClaudeCodeUsageRecord>();
        var date = from;
        var remainingPages = MaximumPages;
        while (true)
        {
            var day = date;
            var dayRecords = await GetAllPagesAsync(
                cursor =>
                    $"/v1/organizations/usage_report/claude_code?starting_at={day:yyyy-MM-dd}&limit=1000{Page(cursor)}",
                root => ParseClaudeCodePage(root, day),
                cancellationToken,
                () =>
                {
                    if (remainingPages == 0)
                    {
                        throw new InvalidDataException($"Anthropic pagination exceeded {MaximumPages} pages.");
                    }
                    remainingPages--;
                }
            );
            records.AddRange(dayRecords);
            if (date == through)
            {
                return Array.AsReadOnly(records.ToArray());
            }
            date = date.PlusDays(1);
        }
    }

    private async Task<IReadOnlyList<T>> GetAllPagesAsync<T>(
        Func<string?, string> buildUrl,
        Func<JsonElement, IReadOnlyList<T>> parsePage,
        CancellationToken cancellationToken,
        Action? consumeReportPage = null
    )
    {
        var records = new List<T>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;

        for (var page = 0; page < MaximumPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            consumeReportPage?.Invoke();
            using var request = new HttpRequestMessage(HttpMethod.Get, buildUrl(cursor));
            using var response = await http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken
            );
            var body = await ReadBoundedAsync(response.Content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if (IsExplicitlyUnavailable(response, body))
                {
                    throw new SourceUnavailableException(
                        "Anthropic reports that this Admin report is unavailable for this organization."
                    );
                }
                response.EnsureSuccessStatusCode();
            }

            using var document = ParseDocument(body);
            var root = RequireObject(document.RootElement, "page");
            records.AddRange(parsePage(root));
            var hasMore = RequireBoolean(root, "has_more");
            var next = RequireCursor(root, hasMore);
            if (!hasMore)
            {
                return Array.AsReadOnly(records.ToArray());
            }
            if (page == MaximumPages - 1)
            {
                throw new InvalidDataException($"Anthropic pagination exceeded {MaximumPages} pages.");
            }
            if (!cursors.Add(next!))
            {
                throw new InvalidDataException("Anthropic pagination repeated a cursor.");
            }
            cursor = next;
        }

        throw new InvalidDataException($"Anthropic pagination exceeded {MaximumPages} pages.");
    }

    private static IReadOnlyList<AnthropicUsageRecord> ParseMessagePage(
        JsonElement root,
        Instant rangeStart,
        Instant rangeEnd
    )
    {
        var records = new List<AnthropicUsageRecord>();
        foreach (var bucket in RequireArray(root, "data").EnumerateArray())
        {
            var (bucketStart, bucketEnd) = ReadDailyBucket(bucket, rangeStart, rangeEnd);
            foreach (var result in RequireArray(bucket, "results").EnumerateArray())
            {
                RequireObject(result, "usage result");
                var model = RequireNullableNonBlankString(result, "model");
                var serviceTier = RequireNullableNonBlankString(result, "service_tier");
                var inferenceGeo = RequireNullableNonBlankString(result, "inference_geo");
                var speed = RequireNullableNonBlankString(result, "speed");
                _ = RequireNullableNonBlankString(result, "account_id");
                _ = RequireNullableNonBlankString(result, "api_key_id");
                _ = RequireNullableNonBlankString(result, "context_window");
                _ = RequireNullableNonBlankString(result, "service_account_id");
                _ = RequireNullableNonBlankString(result, "workspace_id");
                var input = RequireNonNegativeInt64(result, "uncached_input_tokens");
                var output = RequireNonNegativeInt64(result, "output_tokens");
                var cacheRead = RequireNonNegativeInt64(result, "cache_read_input_tokens");
                var cache = RequirePropertyObject(result, "cache_creation");
                var cache5m = RequireNonNegativeInt64(cache, "ephemeral_5m_input_tokens");
                var cache1h = RequireNonNegativeInt64(cache, "ephemeral_1h_input_tokens");
                if (result.TryGetProperty("server_tool_use", out var tools) && tools.ValueKind != JsonValueKind.Null)
                {
                    tools = RequireObject(tools, "server_tool_use");
                    _ = RequireNonNegativeInt64(tools, "web_search_requests");
                }
                _ = checked(cache5m + cache1h);
                records.Add(
                    new AnthropicUsageRecord(
                        bucketStart,
                        bucketEnd,
                        model,
                        serviceTier,
                        inferenceGeo,
                        speed,
                        input,
                        output,
                        cacheRead,
                        cache5m,
                        cache1h,
                        RawEvidence(bucket, result)
                    )
                );
            }
        }
        return records;
    }

    private static IReadOnlyList<AnthropicCostRecord> ParseCostPage(
        JsonElement root,
        Instant rangeStart,
        Instant rangeEnd
    )
    {
        var records = new List<AnthropicCostRecord>();
        foreach (var bucket in RequireArray(root, "data").EnumerateArray())
        {
            var (bucketStart, bucketEnd) = ReadDailyBucket(bucket, rangeStart, rangeEnd);
            foreach (var result in RequireArray(bucket, "results").EnumerateArray())
            {
                RequireObject(result, "cost result");
                var amountText = RequireNonBlankString(result, "amount");
                if (
                    !decimal.TryParse(
                        amountText,
                        NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture,
                        out var amount
                    )
                    || amount < 0m
                )
                {
                    throw new InvalidDataException("Anthropic cost amount is invalid.");
                }
                var currency = RequireNonBlankString(result, "currency");
                if (!string.Equals(currency, "USD", StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Anthropic cost currency is unsupported.");
                }
                records.Add(
                    new AnthropicCostRecord(
                        bucketStart,
                        bucketEnd,
                        amount,
                        currency,
                        RequireNullableNonBlankString(result, "workspace_id"),
                        RequireNullableNonBlankString(result, "description"),
                        RequireNullableNonBlankString(result, "cost_type"),
                        RequireNullableNonBlankString(result, "model"),
                        RequireNullableNonBlankString(result, "context_window"),
                        RequireNullableNonBlankString(result, "inference_geo"),
                        RequireNullableNonBlankString(result, "service_tier"),
                        RequireNullableNonBlankString(result, "token_type"),
                        RawEvidence(bucket, result)
                    )
                );
            }
        }
        return records;
    }

    private static IReadOnlyList<ClaudeCodeUsageRecord> ParseClaudeCodePage(JsonElement root, LocalDate requestedDay)
    {
        var records = new List<ClaudeCodeUsageRecord>();
        foreach (var row in RequireArray(root, "data").EnumerateArray())
        {
            RequireObject(row, "Claude Code usage result");
            var instant = ParseInstant(RequireNonBlankString(row, "date"), "date");
            if (instant != StartOfDay(requestedDay))
            {
                throw new InvalidDataException("Anthropic returned Claude Code usage outside the requested day.");
            }

            var (actorType, actorIdentifier) = ReadActor(row);
            var organizationId = RequireNonBlankString(row, "organization_id");
            var (customerType, subscriptionType) = ReadCustomer(row);
            var isRemote = RequireBoolean(row, "is_remote");
            var terminalType = RequireNonBlankString(row, "terminal_type");
            ValidateClaudeCodeCounts(row);

            foreach (var model in RequireArray(row, "model_breakdown").EnumerateArray())
            {
                RequireObject(model, "model breakdown");
                var modelName = RequireNonBlankString(model, "model");
                var tokens = RequirePropertyObject(model, "tokens");
                var input = RequireNonNegativeInt64(tokens, "input");
                var output = RequireNonNegativeInt64(tokens, "output");
                var cacheRead = RequireNonNegativeInt64(tokens, "cache_read");
                var cacheCreation = RequireNonNegativeInt64(tokens, "cache_creation");
                var (estimatedCost, currency) = ReadEstimatedCost(model);
                records.Add(
                    new ClaudeCodeUsageRecord(
                        requestedDay,
                        actorType,
                        actorIdentifier,
                        organizationId,
                        customerType,
                        subscriptionType,
                        isRemote,
                        terminalType,
                        modelName,
                        input,
                        output,
                        cacheRead,
                        cacheCreation,
                        estimatedCost,
                        currency,
                        RawEvidence(row, model)
                    )
                );
            }
        }
        return records;
    }

    private static (string Type, string Identifier) ReadActor(JsonElement row)
    {
        var actor = RequirePropertyObject(row, "actor");
        var type = RequireNonBlankString(actor, "type");
        var identifier = type switch
        {
            "user_actor" => RequireNonBlankString(actor, "email_address"),
            "api_actor" => RequireNonBlankString(actor, "api_key_name"),
            _ => throw new InvalidDataException("Anthropic returned an unknown Claude Code actor type."),
        };
        return (type, identifier);
    }

    private static (string CustomerType, string? SubscriptionType) ReadCustomer(JsonElement row)
    {
        var customerType = RequireNonBlankString(row, "customer_type");
        if (customerType is not ("api" or "subscription"))
        {
            throw new InvalidDataException("Anthropic returned an unknown Claude Code customer type.");
        }
        var subscriptionType = OptionalNonBlankString(row, "subscription_type");
        if (subscriptionType is not null and not ("enterprise" or "team"))
        {
            throw new InvalidDataException("Anthropic returned an invalid Claude Code subscription type.");
        }
        return (customerType, subscriptionType);
    }

    private static (decimal? Amount, string? Currency) ReadEstimatedCost(JsonElement model)
    {
        if (!model.TryGetProperty("estimated_cost", out var estimated) || estimated.ValueKind == JsonValueKind.Null)
        {
            return (null, null);
        }
        estimated = RequireObject(estimated, "estimated_cost");
        var amount = RequireNonNegativeDecimal(estimated, "amount");
        var currency = RequireNonBlankString(estimated, "currency");
        if (!string.Equals(currency, "USD", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Anthropic Claude Code estimated cost currency is unsupported.");
        }
        return (amount, currency);
    }

    private static void ValidateClaudeCodeCounts(JsonElement row)
    {
        var core = RequirePropertyObject(row, "core_metrics");
        _ = RequireNonNegativeInt64(core, "num_sessions");
        _ = RequireNonNegativeInt64(core, "commits_by_claude_code");
        _ = RequireNonNegativeInt64(core, "pull_requests_by_claude_code");
        var lines = RequirePropertyObject(core, "lines_of_code");
        _ = RequireNonNegativeInt64(lines, "added");
        _ = RequireNonNegativeInt64(lines, "removed");
        var actions = RequirePropertyObject(row, "tool_actions");
        foreach (var action in actions.EnumerateObject())
        {
            var counts = RequireObject(action.Value, "tool action");
            _ = RequireNonNegativeInt64(counts, "accepted");
            _ = RequireNonNegativeInt64(counts, "rejected");
        }
    }

    private static (Instant Start, Instant End) ReadDailyBucket(
        JsonElement bucket,
        Instant rangeStart,
        Instant rangeEnd
    )
    {
        RequireObject(bucket, "bucket");
        var start = ParseInstant(RequireNonBlankString(bucket, "starting_at"), "starting_at");
        var end = ParseInstant(RequireNonBlankString(bucket, "ending_at"), "ending_at");
        if (
            start < rangeStart
            || end > rangeEnd
            || start.InUtc().TimeOfDay != LocalTime.Midnight
            || end.InUtc().TimeOfDay != LocalTime.Midnight
            || end - start != Duration.FromDays(1)
        )
        {
            throw new InvalidDataException("Anthropic returned a bucket outside the requested daily range.");
        }
        return (start, end);
    }

    private static string BuildRangeUrl(string path, Instant start, Instant end, string query, string? cursor) =>
        $"{path}?starting_at={Uri.EscapeDataString(InstantPattern.ExtendedIso.Format(start))}&ending_at={Uri.EscapeDataString(InstantPattern.ExtendedIso.Format(end))}{query}{Page(cursor)}";

    private static string Page(string? cursor) =>
        cursor is null ? string.Empty : $"&page={Uri.EscapeDataString(cursor)}";

    private static Instant StartOfDay(LocalDate day) => day.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();

    private static void ValidateRange(LocalDate from, LocalDate through)
    {
        if (through < from)
        {
            throw new ArgumentException("The end date must not precede the start date.", nameof(through));
        }
    }

    private static Instant ParseInstant(string value, string propertyName)
    {
        var parsed = InstantPattern.ExtendedIso.Parse(value);
        if (!parsed.Success)
        {
            throw new InvalidDataException($"Anthropic {propertyName} must be an RFC 3339 timestamp.");
        }
        return parsed.Value;
    }

    private static JsonDocument ParseDocument(byte[] body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Anthropic returned malformed JSON.", exception);
        }
    }

    private static bool IsExplicitlyUnavailable(HttpResponseMessage response, byte[] body)
    {
        if (response.StatusCode != System.Net.HttpStatusCode.Forbidden)
        {
            return false;
        }
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (
                root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.Object
                || !error.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || !string.Equals(type.GetString(), "permission_error", StringComparison.Ordinal)
                || !error.TryGetProperty("message", out var message)
                || message.ValueKind != JsonValueKind.String
            )
            {
                return false;
            }
            var text = message.GetString()!;
            return text.Contains("not available", StringComparison.OrdinalIgnoreCase)
                || text.Contains("not enabled", StringComparison.OrdinalIgnoreCase)
                || text.Contains("ineligible", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes)
        {
            throw new InvalidDataException("Anthropic response exceeds the size limit.");
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
                    throw new InvalidDataException("Anthropic response exceeds the size limit.");
                }
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static JsonElement RequireObject(JsonElement element, string objectName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Anthropic {objectName} must be a JSON object.");
        }
        return element;
    }

    private static JsonElement RequirePropertyObject(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Anthropic response is missing {propertyName}.");
        }
        return value;
    }

    private static JsonElement RequireArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Anthropic response is missing {propertyName}.");
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
            throw new InvalidDataException($"Anthropic response is missing {propertyName}.");
        }
        return value.GetBoolean();
    }

    private static string? RequireCursor(JsonElement root, bool hasMore)
    {
        if (!root.TryGetProperty("next_page", out var value))
        {
            throw new InvalidDataException("Anthropic response is missing next_page.");
        }
        if (!hasMore)
        {
            if (value.ValueKind != JsonValueKind.Null)
            {
                throw new InvalidDataException("Anthropic final page has an unexpected cursor.");
            }
            return null;
        }
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException("Anthropic response requires a non-empty next_page cursor.");
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
            throw new InvalidDataException($"Anthropic response is missing {propertyName}.");
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
            throw new InvalidDataException($"Anthropic {propertyName} must be a non-empty string or null.");
        }
        return value.GetString();
    }

    private static string? RequireNullableNonBlankString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            throw new InvalidDataException($"Anthropic response is missing {propertyName}.");
        }
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"Anthropic {propertyName} must be a non-empty string or null.");
        }
        return value.GetString();
    }

    private static long RequireNonNegativeInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || !value.TryGetInt64(out var parsed) || parsed < 0)
        {
            throw new InvalidDataException($"Anthropic {propertyName} must be a nonnegative integer.");
        }
        return parsed;
    }

    private static decimal RequireNonNegativeDecimal(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || !value.TryGetDecimal(out var parsed) || parsed < 0m)
        {
            throw new InvalidDataException($"Anthropic {propertyName} must be a nonnegative number.");
        }
        return parsed;
    }

    private static string RawEvidence(JsonElement parent, JsonElement row)
    {
        // The parent's "results" array holds every sibling row; embedding it per record made
        // payloads O(N^2). Keep the bucket's own fields (window bounds) and this row only.
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("bucket");
            WriteWithoutResults(writer, parent);
            writer.WritePropertyName("result");
            row.WriteTo(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }

#pragma warning disable S3267 // Element-ordered writer side effects don't read as a LINQ Where.
    private static void WriteWithoutResults(Utf8JsonWriter writer, JsonElement bucket)
    {
        writer.WriteStartObject();
        foreach (var property in bucket.EnumerateObject())
        {
            if (!property.NameEquals("results"))
            {
                property.WriteTo(writer);
            }
        }
        writer.WriteEndObject();
    }
#pragma warning restore S3267
}
