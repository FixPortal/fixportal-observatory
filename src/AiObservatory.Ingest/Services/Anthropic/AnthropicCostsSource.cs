using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Spend;
using AiObservatory.Ingest.Sources;
using NodaTime;

namespace AiObservatory.Ingest.Services.Anthropic;

public sealed class AnthropicCostsSource(
    IAnthropicAdminClient client,
    BillingObservationWriter writer,
    IClock clock,
    ILogger<AnthropicCostsSource> logger
) : IUsageSource
{
    public string SourceId => UsageSourceIds.AnthropicCostReport;

    public async Task<SourceIngestionResult> IngestAsync(
        LocalDate from,
        LocalDate through,
        CancellationToken cancellationToken
    )
    {
        var records = await client.GetCostsAsync(from, through, cancellationToken);
        var observedAt = clock.GetCurrentInstant();
        foreach (var record in records)
        {
            var occurredOn = record.BucketStart.InUtc().Date;
            var amountUsd = record.AmountFractionalCents / 100m;
            await writer.RecordAsync(
                new BillingObservation
                {
                    ProviderKey = "anthropic",
                    SourceId = SourceId,
                    SourceKind = SourceKind.ProviderApi,
                    UsageScope = UsageScope.Api,
                    CostBasis = CostBasis.Billed,
                    ObservationKey = ObservationKey(record),
                    OccurredOn = occurredOn,
                    BillingPeriod = occurredOn.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                    Service = "Anthropic API",
                    Sku = record.Description,
                    Currency = record.Currency,
                    GrossAmount = amountUsd,
                    CreditAmount = 0m,
                    NetAmount = amountUsd,
                    RawPayload = record.RawJson,
                    ObservedAt = observedAt,
                },
                "anthropic",
                "api-usage",
                cancellationToken
            );
        }

        logger.LogInformation("Anthropic: retained {Count} billed cost observations", records.Count);
        return new SourceIngestionResult(records.Count == 0 ? null : records.Max(record => record.BucketEnd));
    }

    private static string ObservationKey(AnthropicCostRecord record)
    {
        var material = string.Concat(
            Part(record.BucketStart.ToUnixTimeTicks().ToString(CultureInfo.InvariantCulture)),
            Part(record.BucketEnd.ToUnixTimeTicks().ToString(CultureInfo.InvariantCulture)),
            Part(record.WorkspaceId),
            Part(record.Description)
        );
        return $"anthropic:{record.BucketStart.InUtc().Date:yyyy-MM-dd}:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)))}";
    }

    private static string Part(string? value) =>
        value is null ? "-1:" : $"{value.Length.ToString(CultureInfo.InvariantCulture)}:{value}";
}
