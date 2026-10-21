using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Spend;
using AiObservatory.Ingest.Sources;
using NodaTime;

namespace AiObservatory.Ingest.Services.OpenAi;

public sealed class OpenAiCostsSource(
    IOpenAiAdminClient client,
    BillingObservationWriter writer,
    IClock clock,
    ILogger<OpenAiCostsSource> logger
) : IUsageSource
{
    public string SourceId => UsageSourceIds.OpenAiCostsApi;

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
            await writer.RecordAsync(
                new BillingObservation
                {
                    ProviderKey = "openai",
                    SourceId = SourceId,
                    SourceKind = SourceKind.ProviderApi,
                    UsageScope = UsageScope.Api,
                    CostBasis = CostBasis.Billed,
                    ObservationKey = ObservationKey(record),
                    OccurredOn = occurredOn,
                    BillingPeriod = occurredOn.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                    Service = "OpenAI API",
                    Sku = record.LineItem,
                    Currency = record.Currency,
                    GrossAmount = record.Amount,
                    CreditAmount = 0m,
                    NetAmount = record.Amount,
                    RawPayload = record.RawJson,
                    ObservedAt = observedAt,
                },
                "openai",
                "api-usage",
                cancellationToken
            );
        }

        logger.LogInformation("OpenAI: retained {Count} billed cost observations", records.Count);
        return new SourceIngestionResult(records.Count == 0 ? null : records.Max(record => record.BucketEnd));
    }

    private static string ObservationKey(OpenAiCostRecord record)
    {
        var material = string.Concat(
            Part(record.BucketStart.ToUnixTimeTicks().ToString(CultureInfo.InvariantCulture)),
            Part(record.BucketEnd.ToUnixTimeTicks().ToString(CultureInfo.InvariantCulture)),
            Part(record.ProjectId),
            Part(record.LineItem)
        );
        return $"openai:{record.BucketStart.InUtc().Date:yyyy-MM-dd}:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)))}";
    }

    private static string Part(string? value) =>
        value is null ? "-1:" : $"{value.Length.ToString(CultureInfo.InvariantCulture)}:{value}";
}
