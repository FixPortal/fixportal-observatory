using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using AiObservatory.Ingest.Sources;
using NodaTime;

namespace AiObservatory.Ingest.Services.Anthropic;

public sealed class AnthropicUsageSource(
    IAnthropicAdminClient client,
    IUsageRepository repository,
    IClock clock,
    ILogger<AnthropicUsageSource> logger
) : IUsageSource
{
    public string SourceId => UsageSourceIds.AnthropicUsageApi;

    public async Task<SourceIngestionResult> IngestAsync(
        LocalDate from,
        LocalDate through,
        CancellationToken cancellationToken
    )
    {
        var records = await client.GetMessageUsageAsync(from, through, cancellationToken);
        var groups = records
            .GroupBy(record => new
            {
                record.BucketStart,
                record.BucketEnd,
                record.Model,
                record.ServiceTier,
                record.InferenceGeo,
                record.Speed,
            })
            .ToArray();
        var observedAt = clock.GetCurrentInstant();

        foreach (var group in groups)
        {
            var rows = group.ToArray();
            var cache5m = rows.Sum(row => row.CacheWrite5mTokens);
            var cache1h = rows.Sum(row => row.CacheWrite1hTokens);
            await repository.RecordEstimatedEventAsync(
                new UsageEvent
                {
                    Provider = Provider.Anthropic,
                    OccurredAt = group.Key.BucketStart,
                    IngestedAt = observedAt,
                    Model = group.Key.Model,
                    InputTokens = rows.Sum(row => row.InputTokens),
                    OutputTokens = rows.Sum(row => row.OutputTokens),
                    CacheReadTokens = rows.Sum(row => row.CacheReadTokens),
                    CacheWriteTokens = checked(cache5m + cache1h),
                    CacheWrite1hTokens = cache1h,
                    CostUsd = null,
                    EventKey = EventKey(
                        group.Key.BucketStart,
                        group.Key.BucketEnd,
                        group.Key.Model,
                        group.Key.ServiceTier,
                        group.Key.InferenceGeo,
                        group.Key.Speed
                    ),
                    RawPayload = JsonSerializer.Serialize(
                        new
                        {
                            service_tier = group.Key.ServiceTier,
                            inference_geo = group.Key.InferenceGeo,
                            speed = group.Key.Speed,
                            cache_creation = new
                            {
                                ephemeral_5m_input_tokens = cache5m,
                                ephemeral_1h_input_tokens = cache1h,
                            },
                            provider_records = rows.Select(row => JsonSerializer.Deserialize<JsonElement>(row.RawJson)),
                        }
                    ),
                    SourceId = SourceId,
                    SourceKind = SourceKind.ProviderApi,
                    UsageScope = UsageScope.Api,
                    CostBasis = CostBasis.ListPriceEstimate,
                    ObservedAt = observedAt,
                },
                cancellationToken
            );
        }

        logger.LogInformation(
            "Anthropic: ingested {Count} message usage records into {GroupCount} price-dimension groups",
            records.Count,
            groups.Length
        );
        return new SourceIngestionResult(records.Count == 0 ? null : records.Max(record => record.BucketEnd));
    }

    private static string EventKey(
        Instant start,
        Instant end,
        string? model,
        string? serviceTier,
        string? inferenceGeo,
        string? speed
    )
    {
        var material = string.Concat(
            Part(start.ToUnixTimeTicks().ToString(CultureInfo.InvariantCulture)),
            Part(end.ToUnixTimeTicks().ToString(CultureInfo.InvariantCulture)),
            Part(model),
            Part(serviceTier),
            Part(inferenceGeo),
            Part(speed)
        );
        return $"anthropic:{start.InUtc().Date:yyyy-MM-dd}:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)))}";
    }

    private static string Part(string? value) =>
        value is null ? "-1:" : $"{value.Length.ToString(CultureInfo.InvariantCulture)}:{value}";
}
