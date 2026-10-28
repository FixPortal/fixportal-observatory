using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
        foreach (var record in records)
        {
            await writer.RecordAsync(
                new BillingObservation
                {
                    ProviderKey = "moonshot",
                    SourceId = SourceId,
                    SourceKind = SourceKind.ProviderApi,
                    UsageScope = UsageScope.Api,
                    CostBasis = CostBasis.Billed,
                    ObservationKey = ObservationKey(record),
                    OccurredOn = record.Date,
                    BillingPeriod = record.Date.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                    Service = "Moonshot API",
                    Sku = record.Sku,
                    Currency = "USD",
                    GrossAmount = record.TotalUsd,
                    CreditAmount = -record.VoucherUsd,
                    NetAmount = record.CashUsd,
                    RawPayload = record.RawJson,
                    ObservedAt = observedAt,
                },
                "moonshot",
                "api-usage",
                cancellationToken
            );
        }

        logger.LogInformation("Moonshot: retained {Count} billed cost observations", records.Count);
        return new SourceIngestionResult(
            records.Count == 0
                ? null
                : records.Max(record => record.Date).PlusDays(1).AtStartOfDayInZone(BillingZone).ToInstant()
        );
    }

    private static string ObservationKey(MoonshotBillRecord record)
    {
        var material = string.Concat(Part(record.ModelId), Part(record.TokenType), Part(record.KeyId));
        return $"moonshot:{record.Date:yyyy-MM-dd}:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)))}";
    }

    private static string Part(string? value) =>
        value is null ? "-1:" : $"{value.Length.ToString(CultureInfo.InvariantCulture)}:{value}";
}
