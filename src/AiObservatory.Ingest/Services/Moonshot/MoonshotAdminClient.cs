using System.Buffers;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiObservatory.Ingest.Sources;
using NodaTime;
using NodaTime.Text;

namespace AiObservatory.Ingest.Services.Moonshot;

/// <summary>
/// Reads Moonshot organization daily bills (<c>GET /v1/admin/organizations/billings/daily_bills</c>,
/// Admin key with the <c>billing:read</c> scope). Bill dates and day windows are Asia/Shanghai calendar days;
/// amounts arrive in milli-cent (100,000 milli-cent = 1 USD).
/// </summary>
public sealed class MoonshotAdminClient(HttpClient http) : IMoonshotAdminClient
{
    private const string Path = "/v1/admin/organizations/billings/daily_bills";
    private const string Provider = "Moonshot";
    private const int MaximumPages = 1_000;
    private const int MaximumResponseBytes = 8 * 1024 * 1024;
    private const decimal MilliCentPerUsd = 100_000m;

    private static readonly DateTimeZone BillingZone = DateTimeZone.ForOffset(Offset.FromHours(8));

    public async Task<IReadOnlyList<MoonshotBillRecord>> GetDailyBillsAsync(
        LocalDate from,
        LocalDate through,
        CancellationToken cancellationToken = default
    )
    {
        if (through < from)
        {
            throw new ArgumentException("The end date must not precede the start date.", nameof(through));
        }

        var start = ToUnixMilliseconds(from);
        var end = ToUnixMilliseconds(through.PlusDays(1));
        var records = new List<MoonshotBillRecord>();
        var seenDates = new HashSet<LocalDate>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;

        for (var page = 0; page < MaximumPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = string.Create(
                CultureInfo.InvariantCulture,
                $"{Path}?start={start}&end={end}&limit=1000{(cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}")}"
            );
            var body = await GetPageBodyAsync(url, page, cancellationToken);
            if (body is null)
            {
                return Array.AsReadOnly(records.ToArray());
            }

            var next = ParsePage(body, from, through, seenDates, records);
            if (next is null)
            {
                return Array.AsReadOnly(records.ToArray());
            }
            if (!cursors.Add(next))
            {
                throw new InvalidDataException("Moonshot pagination repeated a cursor.");
            }
            cursor = next;
        }

        throw new InvalidDataException($"Moonshot pagination exceeded {MaximumPages} pages.");
    }

    /// <summary>Returns the page body, or null when Moonshot reports no data for the organization.</summary>
    private async Task<byte[]?> GetPageBodyAsync(string url, int page, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await ReadBoundedAsync(response.Content, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return body;
        }
        if (IsNoDataForOrganization(response.StatusCode, body, page))
        {
            return null;
        }
        throw FailureFor(response.StatusCode, body);
    }

    /// <summary>Adds the page's bill lines to <paramref name="records"/> and returns the next cursor, or null on the last page.</summary>
    private static string? ParsePage(
        byte[] body,
        LocalDate from,
        LocalDate through,
        HashSet<LocalDate> seenDates,
        List<MoonshotBillRecord> records
    )
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Moonshot returned malformed JSON.", exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Moonshot page must be a JSON object.");
            }

            foreach (var bill in RequireArray(root, "data").EnumerateArray())
            {
                ParseDay(bill, from, through, seenDates, records);
            }

            return RequireCursor(root, RequireBoolean(root, "has_more"));
        }
    }

    private static void ParseDay(
        JsonElement bill,
        LocalDate from,
        LocalDate through,
        HashSet<LocalDate> seenDates,
        List<MoonshotBillRecord> records
    )
    {
        if (bill.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Moonshot daily bill must be a JSON object.");
        }

        var dateText = AdminResponseJson.RequireNonBlankString(bill, "date", Provider);
        var parsed = LocalDatePattern.Iso.Parse(dateText);
        if (!parsed.Success || parsed.Value < from || parsed.Value > through)
        {
            throw new InvalidDataException("Moonshot returned a bill outside the requested date range.");
        }
        var date = parsed.Value;
        if (!seenDates.Add(date))
        {
            throw new InvalidDataException("Moonshot returned more than one bill for a date.");
        }

        var total = RequireFee(bill, "total_fee");
        var cash = RequireFee(bill, "recharge_fee");
        var voucher = RequireFee(bill, "voucher_fee");
        RequireSplit(total, cash, voucher);

        long detailTotal = 0;
        long detailCash = 0;
        long detailVoucher = 0;
        // The documented grain is one row per model x token type x key. A repeat would share an
        // observation key and the second row would silently replace the first, so refuse it.
        var seenDetails = new HashSet<(string?, string?, string?)>();
        foreach (var detail in RequireArray(bill, "model_details").EnumerateArray())
        {
            var (record, fees) = ParseDetail(detail, date, dateText);
            if (!seenDetails.Add((record.ModelId, record.TokenType, record.KeyId)))
            {
                throw new InvalidDataException("Moonshot returned duplicate model details for a day.");
            }

            detailTotal = checked(detailTotal + fees.Total);
            detailCash = checked(detailCash + fees.Cash);
            detailVoucher = checked(detailVoucher + fees.Voucher);
            records.Add(record);
        }

        // The outer fees also carry charges the breakdown leaves out (cache calls, tool calls, cache
        // storage, batch). Keep them as their own line so the day still sums to the billed total.
        var residualTotal = total - detailTotal;
        var residualCash = cash - detailCash;
        var residualVoucher = voucher - detailVoucher;
        if (residualTotal < 0 || residualCash < 0 || residualVoucher < 0)
        {
            throw new InvalidDataException("Moonshot model details exceed the daily bill total.");
        }
        if (residualTotal > 0)
        {
            records.Add(
                new MoonshotBillRecord(
                    date,
                    null,
                    null,
                    null,
                    null,
                    ToUsd(residualTotal),
                    ToUsd(residualCash),
                    ToUsd(residualVoucher),
                    new JsonObject
                    {
                        ["date"] = dateText,
                        ["residual"] = new JsonObject
                        {
                            ["total_fee"] = residualTotal,
                            ["recharge_fee"] = residualCash,
                            ["voucher_fee"] = residualVoucher,
                        },
                    }.ToJsonString()
                )
            );
        }
    }

    private static (MoonshotBillRecord Record, (long Total, long Cash, long Voucher) Fees) ParseDetail(
        JsonElement detail,
        LocalDate date,
        string dateText
    )
    {
        if (detail.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Moonshot model detail must be a JSON object.");
        }

        var total = RequireFee(detail, "total_fee");
        var cash = RequireFee(detail, "recharge_fee");
        var voucher = RequireFee(detail, "voucher_fee");
        RequireSplit(total, cash, voucher);

        var record = new MoonshotBillRecord(
            date,
            AdminResponseJson.RequireNonBlankString(detail, "model_id", Provider),
            AdminResponseJson.RequireNonBlankString(detail, "token_type", Provider),
            AdminResponseJson.RequireNonBlankString(detail, "key", Provider),
            RequireNonNegativeInt64(detail, "tokens"),
            ToUsd(total),
            ToUsd(cash),
            ToUsd(voucher),
            new JsonObject { ["date"] = dateText, ["detail"] = JsonNode.Parse(detail.GetRawText()) }.ToJsonString()
        );
        return (record, (total, cash, voucher));
    }

    private static bool IsNoDataForOrganization(HttpStatusCode status, byte[] body, int page) =>
        page == 0 && status == HttpStatusCode.NotFound && ErrorCode(body) == "resource_not_found";

    private static Exception FailureFor(HttpStatusCode status, byte[] body)
    {
        var code = ErrorCode(body);
        // Today's bill (and yesterday's before 07:00 CST) is not ready: retry on the next poll without
        // counting it as a failure.
        if (status == HttpStatusCode.BadRequest && code == "data_not_ready")
        {
            return new SourceUnavailableException("Moonshot billing data is not ready for the requested date range.");
        }

        var detail = code is null ? string.Empty : $" ({code})";
        return new HttpRequestException(
            $"Moonshot returned HTTP {(int)status}{detail}.",
            inner: null,
            statusCode: status
        );
    }

    private static string? ErrorCode(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return
                document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.String
                ? code.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static long ToUnixMilliseconds(LocalDate date) =>
        date.AtStartOfDayInZone(BillingZone).ToInstant().ToUnixTimeMilliseconds();

    private static decimal ToUsd(long milliCent) => milliCent / MilliCentPerUsd;

    private static void RequireSplit(long total, long cash, long voucher)
    {
        if (checked(cash + voucher) != total)
        {
            throw new InvalidDataException("Moonshot recharge_fee plus voucher_fee does not equal total_fee.");
        }
    }

    private static string? RequireCursor(JsonElement root, bool hasMore)
    {
        if (!root.TryGetProperty("next", out var value))
        {
            throw new InvalidDataException("Moonshot response is missing next.");
        }
        if (!hasMore)
        {
            if (value.ValueKind != JsonValueKind.Null)
            {
                throw new InvalidDataException("Moonshot final page has an unexpected cursor.");
            }
            return null;
        }
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException("Moonshot response requires a non-empty next cursor.");
        }
        return value.GetString();
    }

    private static JsonElement RequireArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Moonshot response is missing {propertyName}.");
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
            throw new InvalidDataException($"Moonshot response is missing {propertyName}.");
        }
        return value.GetBoolean();
    }

    private static long RequireFee(JsonElement element, string propertyName) =>
        RequireNonNegativeInt64(element, propertyName);

    private static long RequireNonNegativeInt64(JsonElement element, string propertyName)
    {
        // The ValueKind guard comes first: TryGetInt64 throws InvalidOperationException for a
        // non-number kind, bypassing the documented InvalidDataException contract.
        if (
            !element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var parsed)
        )
        {
            throw new InvalidDataException($"Moonshot response is missing {propertyName}.");
        }
        if (parsed < 0)
        {
            throw new InvalidDataException($"Moonshot {propertyName} cannot be negative.");
        }
        return parsed;
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes)
        {
            throw new InvalidDataException("Moonshot response exceeds the size limit.");
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
                    throw new InvalidDataException("Moonshot response exceeds the size limit.");
                }
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
