using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Spend;
using AiObservatory.Ingest.Sources;
using NodaTime;

namespace AiObservatory.Ingest.Services.Moonshot;

/// <summary>
/// Billed Moonshot API spend from the organization's daily bills. Gross is the day's total fee, the credit is the
/// voucher part (negative), and net is the cash deducted from the recharge account.
/// </summary>
public sealed class MoonshotBillingSource(
    IMoonshotAdminClient client,
    BillingObservationWriter writer,
    IClock clock,
    ILogger<MoonshotBillingSource> logger
) : IUsageSource
{
    public const string SubLedgerSku = "lines below ledger precision";

    // The ledger holds GBP to four decimals and the writer refuses a net amount that rounds to zero there. A single
    // Moonshot line can be that small (a few hundred cache-hit tokens bill about 4 milli-cent), and one refusal would
    // abort the whole ingest on every poll. 0.0001 USD stays non-zero at any USD to GBP rate above 0.5.
    private const decimal LedgerFloorUsd = 0.0001m;

    private static readonly DateTimeZone BillingZone = DateTimeZone.ForOffset(Offset.FromHours(8));

    public string SourceId => UsageSourceIds.MoonshotBillingApi;

    public async Task<SourceIngestionResult> IngestAsync(
        LocalDate from,
        LocalDate through,
        CancellationToken cancellationToken
    )
    {
        var records = await client.GetDailyBillsAsync(from, through, cancellationToken);
        var observedAt = clock.GetCurrentInstant();
        var retained = 0;
        foreach (var observation in ToObservations(records, observedAt))
        {
            await writer.RecordAsync(observation, "moonshot", "api-usage", cancellationToken);
            retained++;
        }

        logger.LogInformation("Moonshot: retained {Count} billed cost observations", retained);
        return new SourceIngestionResult(
            records.Count == 0
                ? null
                : records.Max(record => record.Date).PlusDays(1).AtStartOfDayInZone(BillingZone).ToInstant()
        );
    }

    private IEnumerable<BillingObservation> ToObservations(
        IReadOnlyList<MoonshotBillRecord> records,
        Instant observedAt
    )
    {
        foreach (var day in records.GroupBy(record => record.Date))
        {
            var lines = day.ToList();
            var tooSmall = lines.Where(IsBelowLedgerFloor).ToList();
            foreach (var record in lines.Except(tooSmall))
            {
                yield return Observation(record, observedAt);
            }

            if (tooSmall.Count > 0 && FoldedObservation(day.Key, tooSmall, observedAt) is { } folded)
            {
                yield return folded;
            }
        }
    }

    // A line with no cash (covered by a voucher) is always recordable: the writer takes a zero net as an observation
    // without a spend row. Only a positive cash amount under the floor cannot be stored on its own.
    private static bool IsBelowLedgerFloor(MoonshotBillRecord record) =>
        record.CashUsd > 0m && record.CashUsd < LedgerFloorUsd;

    private BillingObservation? FoldedObservation(
        LocalDate date,
        IReadOnlyList<MoonshotBillRecord> lines,
        Instant observedAt
    )
    {
        var cash = lines.Sum(line => line.CashUsd);
        if (cash < LedgerFloorUsd)
        {
            logger.LogInformation(
                "Moonshot: {Count} line(s) on {Date} total {Cash} USD, below the ledger precision, and were not recorded",
                lines.Count,
                date,
                cash
            );
            return null;
        }

        var raw = new JsonObject
        {
            ["date"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["folded"] = new JsonArray([.. lines.Select(line => JsonNode.Parse(line.RawJson))]),
        };
        return Observation(
            date,
            Key(date, Part(SubLedgerSku)),
            SubLedgerSku,
            lines.Sum(line => line.TotalUsd),
            lines.Sum(line => line.VoucherUsd),
            cash,
            raw.ToJsonString(),
            observedAt
        );
    }

    private static BillingObservation Observation(MoonshotBillRecord record, Instant observedAt) =>
        Observation(
            record.Date,
            Key(record.Date, string.Concat(Part(record.ModelId), Part(record.TokenType), Part(record.KeyId))),
            record.Sku,
            record.TotalUsd,
            record.VoucherUsd,
            record.CashUsd,
            record.RawJson,
            observedAt
        );

    private static BillingObservation Observation(
        LocalDate date,
        string key,
        string sku,
        decimal totalUsd,
        decimal voucherUsd,
        decimal cashUsd,
        string rawJson,
        Instant observedAt
    ) =>
        new()
        {
            ProviderKey = "moonshot",
            SourceId = UsageSourceIds.MoonshotBillingApi,
            SourceKind = SourceKind.ProviderApi,
            UsageScope = UsageScope.Api,
            CostBasis = CostBasis.Billed,
            ObservationKey = key,
            OccurredOn = date,
            BillingPeriod = date.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            Service = "Moonshot API",
            Sku = sku,
            Currency = "USD",
            GrossAmount = totalUsd,
            CreditAmount = -voucherUsd,
            NetAmount = cashUsd,
            RawPayload = rawJson,
            ObservedAt = observedAt,
        };

    private static string Key(LocalDate date, string material) =>
        $"moonshot:{date:yyyy-MM-dd}:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)))}";

    private static string Part(string? value) =>
        value is null ? "-1:" : $"{value.Length.ToString(CultureInfo.InvariantCulture)}:{value}";
}
