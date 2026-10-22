using System.Text.Json.Serialization;

namespace AiObservatory.Audit.Contracts;

public sealed record FrozenEvent(
    Guid RowId,
    string? EventKey,
    string SourceId,
    ObservationProvenance Provenance,
    DateTimeOffset OccurredAt,
    DateTimeOffset ObservedAt,
    DateTimeOffset IngestedAt,
    string Provider,
    string? Model,
    UsageCounters Tokens,
    decimal? CostUsd,
    decimal? CacheSavingsUsd,
    string? Runtime,
    string? SessionId,
    string? AgentId,
    DateTimeOffset? CorrectedAt,
    string RevisionDigest,
    PricingApplication? PricingApplication
)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PricingDimensions? SourceDimensions { get; init; }
}

public sealed record UsageCounters(
    long InputTokens,
    long OutputTokens,
    long? CacheReadTokens,
    long? CacheWriteTokens,
    long? CacheWrite1hTokens,
    long? ThoughtTokens
);

public sealed record PricingApplication(
    string? SnapshotId,
    string? SnapshotHash,
    string RuleId,
    PricingDimensions Dimensions,
    DateOnly PricingDate
)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Kind { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RuleVersion { get; init; }
}
