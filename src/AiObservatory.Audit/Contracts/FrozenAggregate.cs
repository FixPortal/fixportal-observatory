namespace AiObservatory.Audit.Contracts;

public sealed record FrozenAggregate(
    AggregateKey Key,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long CacheWrite1hTokens,
    decimal CostUsd,
    long UnknownCostCount,
    decimal CacheSavingsUsd,
    long UnknownCacheSavingsCount,
    long RequestCount
);

/// <summary>Every production grouping dimension participates in the identity, including provenance.</summary>
public sealed record AggregateKey(
    DateOnly Date,
    string Provider,
    string Model,
    string SourceId,
    SourceKind SourceKind,
    UsageScope UsageScope,
    AccountingBasis CostBasis
);
