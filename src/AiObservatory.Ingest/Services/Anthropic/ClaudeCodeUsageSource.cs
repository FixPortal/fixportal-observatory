using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using AiObservatory.Ingest.Sources;
using NodaTime;

namespace AiObservatory.Ingest.Services.Anthropic;

public sealed class ClaudeCodeUsageSource(
    IAnthropicAdminClient client,
    IUsageRepository repository,
    IClock clock,
    ILogger<ClaudeCodeUsageSource> logger
) : IUsageSource
{
    public string SourceId => UsageSourceIds.ClaudeCodeUsageApi;

    public async Task<SourceIngestionResult> IngestAsync(
        LocalDate from,
        LocalDate through,
        CancellationToken cancellationToken
    )
    {
        var records = await client.GetClaudeCodeUsageAsync(from, through, cancellationToken);
        var groups = records
            .GroupBy(record => new
            {
                record.Date,
                record.ActorType,
                record.ActorIdentifier,
                record.OrganizationId,
                record.CustomerType,
                record.IsRemote,
                record.TerminalType,
                record.Model,
            })
            .ToArray();
        var observedAt = clock.GetCurrentInstant();

        foreach (var group in groups)
        {
            var rows = group.ToArray();
            decimal? estimatedMinor = rows.All(row => row.EstimatedCostMinor is not null)
                ? rows.Sum(row => row.EstimatedCostMinor!.Value)
                : null;
            await repository.RecordEventAsync(
                new UsageEvent
                {
                    Provider = Provider.Anthropic,
                    OccurredAt = group.Key.Date.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant(),
                    IngestedAt = observedAt,
                    Model = group.Key.Model,
                    InputTokens = rows.Sum(row => row.InputTokens),
                    OutputTokens = rows.Sum(row => row.OutputTokens),
                    CacheReadTokens = rows.Sum(row => row.CacheReadTokens),
                    CacheWriteTokens = rows.Sum(row => row.CacheCreationTokens),
                    CostUsd = estimatedMinor / 100m,
                    EventKey = EventKey(
                        group.Key.Date,
                        group.Key.ActorType,
                        group.Key.ActorIdentifier,
                        group.Key.OrganizationId,
                        group.Key.CustomerType,
                        group.Key.IsRemote,
                        group.Key.TerminalType,
                        group.Key.Model
                    ),
                    RawPayload = JsonSerializer.Serialize(
                        new
                        {
                            actor = new { type = group.Key.ActorType, identifier = group.Key.ActorIdentifier },
                            organization_id = group.Key.OrganizationId,
                            customer_type = group.Key.CustomerType,
                            // A mid-day resubscription can put two distinct values in one group.
                            // Keep the first rather than failing the whole day's ingest.
                            subscription_type = rows.Select(row => row.SubscriptionType).Distinct().FirstOrDefault(),
                            is_remote = group.Key.IsRemote,
                            terminal_type = group.Key.TerminalType,
                            estimated_cost_minor = estimatedMinor,
                            provider_records = rows.Select(row => JsonSerializer.Deserialize<JsonElement>(row.RawJson)),
                        }
                    ),
                    SourceId = SourceId,
                    SourceKind = SourceKind.ProviderApi,
                    UsageScope = group.Key.CustomerType == "api" ? UsageScope.Api : UsageScope.Subscription,
                    CostBasis = estimatedMinor is null ? CostBasis.None : CostBasis.ProviderEstimated,
                    ObservedAt = observedAt,
                },
                cancellationToken
            );
        }

        logger.LogInformation(
            "Anthropic: ingested {Count} Claude Code model records into {GroupCount} semantic lanes",
            records.Count,
            groups.Length
        );
        return new SourceIngestionResult(
            records.Count == 0
                ? null
                : records.Max(record => record.Date).PlusDays(1).AtStartOfDayInZone(DateTimeZone.Utc).ToInstant()
        );
    }

    private static string EventKey(
        LocalDate date,
        string actorType,
        string actorIdentifier,
        string organizationId,
        string customerType,
        bool isRemote,
        string terminalType,
        string model
    )
    {
        var material = string.Concat(
            Part(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            Part(actorType),
            Part(actorIdentifier),
            Part(organizationId),
            Part(customerType),
            Part(isRemote.ToString()),
            Part(terminalType),
            Part(model)
        );
        return $"claude-code:{date:yyyy-MM-dd}:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)))}";
    }

    private static string Part(string value) => $"{value.Length.ToString(CultureInfo.InvariantCulture)}:{value}";
}
