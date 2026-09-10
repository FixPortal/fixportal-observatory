using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using AiObservatory.Data.Spend;
using AiObservatory.Ingest.Sources;
using NodaTime;

namespace AiObservatory.Ingest.Services.Google;

public sealed class GoogleBillingExportSource(
    IGoogleBillingExportClient client,
    SourceSyncStateStore states,
    BillingObservationWriter writer,
    ILogger<GoogleBillingExportSource> logger
) : IUsageSource
{
    public string SourceId => UsageSourceIds.GoogleCloudBillingExport;

    public async Task<SourceIngestionResult> IngestAsync(
        LocalDate from,
        LocalDate through,
        CancellationToken cancellationToken
    )
    {
        var fromInstant = from.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();
        var throughExclusive = through.PlusDays(1).AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();
        var previous = await states.GetAsync(SourceId, cancellationToken);
        var changesSince = previous?.LatestObservationAt ?? fromInstant;
        var result = await client.GetBillingRecordsAsync(
            fromInstant,
            throughExclusive,
            changesSince,
            cancellationToken
        );
        if (result.OutOfRangeAffectedKeyCount is null)
        {
            // The companion count query failed and the client degraded to "unavailable": the
            // records below are complete, but a late correction older than the scan floor would
            // go silently stale this cycle. Distinct from the stale-correction warning so an
            // operator can tell "detection broken" from "corrections present" — and logged
            // WITH the failure so the cause is not lost.
            logger.LogWarning(
                result.OutOfRangeCountFailure,
                "Google: the out-of-range billing correction key count was unavailable; stale corrections affecting usage older than the 31-day scan floor cannot be detected in this cycle"
            );
        }
        else if (result.OutOfRangeAffectedKeyCount > 0)
        {
            // A correction exported today for usage older than the 31-day scan floor is an
            // affected key the line_items query can never satisfy: no rows, empty aggregate,
            // and the stored observation for that key silently stays stale. Surface it.
            logger.LogWarning(
                "Google: {Count} billing correction key(s) exported after {ChangesSince} affect usage older than the 31-day scan floor; their stored observations cannot be refreshed and will stay stale",
                result.OutOfRangeAffectedKeyCount,
                changesSince
            );
        }
        var records = result.Records;
        foreach (var record in records)
        {
            await writer.RecordAsync(
                new BillingObservation
                {
                    ProviderKey = "google",
                    SourceId = SourceId,
                    SourceKind = SourceKind.ProviderApi,
                    UsageScope = UsageScope.Api,
                    CostBasis = CostBasis.Billed,
                    ObservationKey = ObservationKey(record),
                    OccurredOn = record.UsageDate,
                    BillingPeriod = record.BillingPeriod,
                    Service = record.ServiceDescription,
                    Sku = record.SkuDescription,
                    Currency = record.Currency,
                    GrossAmount = record.GrossAmount,
                    CreditAmount = record.CreditAmount,
                    NetAmount = record.NetAmount,
                    RawPayload = record.RawJson,
                    ObservedAt = record.ObservedAt,
                },
                "google",
                "cloud",
                cancellationToken
            );
        }
        logger.LogInformation("Google: retained {Count} BigQuery billing observations", records.Count);
        return new SourceIngestionResult(records.Count == 0 ? null : records.Max(record => record.ObservedAt));
    }

    private static string ObservationKey(GoogleBillingRecord record)
    {
        var material = string.Concat(
            Part(record.UsageDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            Part(record.BillingPeriod),
            Part(record.ServiceId),
            Part(record.SkuId),
            Part(record.Currency)
        );
        return $"google:{record.UsageDate:yyyy-MM-dd}:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)))}";
    }

    private static string Part(string value) => $"{value.Length.ToString(CultureInfo.InvariantCulture)}:{value}";
}
