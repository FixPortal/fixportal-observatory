using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using AiObservatory.Ingest.Sources;
using NodaTime;

namespace AiObservatory.Ingest.Services.OpenAi;

public sealed class OpenAiUsageSource(
    IOpenAiAdminClient client,
    IUsageRepository repository,
    IClock clock,
    ILogger<OpenAiUsageSource> logger
) : IUsageSource
{
    public string SourceId => UsageSourceIds.OpenAiUsageApi;

    public async Task<SourceIngestionResult> IngestAsync(
        LocalDate from,
        LocalDate through,
        CancellationToken cancellationToken
    )
    {
        var records = await client.GetUsageAsync(from, through, cancellationToken);
        var groups = records
            .GroupBy(record => new
            {
                record.BucketStart,
                record.BucketEnd,
                record.Model,
                record.Batch,
                record.ServiceTier,
                record.Processing,
            })
            .ToArray();
        var observedAt = clock.GetCurrentInstant();

        foreach (var group in groups)
        {
            var rows = group.ToArray();
            var usage = new UsageEvent
            {
                Provider = Provider.OpenAI,
                OccurredAt = group.Key.BucketStart,
                IngestedAt = observedAt,
                Model = group.Key.Model,
                InputTokens = rows.Sum(row => row.InputUncachedTokens),
                OutputTokens = rows.Sum(row => row.OutputTokens),
                CacheReadTokens = rows.Sum(row => row.InputCachedTokens),
                CacheWriteTokens = rows.Sum(row => row.InputCacheWriteTokens),
                CostUsd = null,
                EventKey = EventKey(
                    group.Key.BucketStart,
                    group.Key.BucketEnd,
                    group.Key.Model,
                    group.Key.Batch,
                    group.Key.ServiceTier
                ),
                RawPayload = JsonSerializer.Serialize(
                    new
                    {
                        batch = group.Key.Batch,
                        service_tier = group.Key.ServiceTier,
                        processing = group.Key.Processing,
                        model_requests = rows.Sum(row => row.ModelRequests),
                        provider_records = rows.Select(row => JsonSerializer.Deserialize<JsonElement>(row.RawJson)),
                    }
                ),
                SourceId = SourceId,
                SourceKind = SourceKind.ProviderApi,
                UsageScope = UsageScope.Api,
                CostBasis = CostBasis.ListPriceEstimate,
                ObservedAt = observedAt,
            };
            await repository.RecordEstimatedEventAsync(usage, cancellationToken);
        }

        logger.LogInformation(
            "OpenAI: ingested {Count} usage records into {GroupCount} price-dimension groups",
            records.Count,
            groups.Length
        );
        return new SourceIngestionResult(records.Count == 0 ? null : records.Max(record => record.BucketEnd));
    }

    private static string EventKey(Instant start, Instant end, string? model, bool? batch, string? serviceTier)
    {
        var material = string.Concat(
            Part(start.ToUnixTimeTicks().ToString(CultureInfo.InvariantCulture)),
            Part(end.ToUnixTimeTicks().ToString(CultureInfo.InvariantCulture)),
            Part(model),
            Part(batch?.ToString()),
            Part(serviceTier)
        );
        return $"openai:{start.InUtc().Date:yyyy-MM-dd}:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)))}";
    }

    private static string Part(string? value) =>
        value is null ? "-1:" : $"{value.Length.ToString(CultureInfo.InvariantCulture)}:{value}";
}
