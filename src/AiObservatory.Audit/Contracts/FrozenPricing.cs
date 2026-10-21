namespace AiObservatory.Audit.Contracts;

/// <summary>Typed pricing facts preserve effective windows and source identity without arbitrary catalog JSON.</summary>
public sealed record FrozenPricing(
    string SourceId,
    string Id,
    string ContentHash,
    string Provider,
    string Currency,
    DateTimeOffset RetrievedAt,
    IReadOnlyList<FrozenPriceRate> Rates
);

public sealed record FrozenPriceRate(
    string Id,
    string Model,
    IReadOnlyList<string> Aliases,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    bool EffectiveDateIsProviderDeclared,
    PriceMeter Meter,
    PricingDimensions Dimensions,
    long? ContextThreshold,
    long UnitTokens,
    decimal UnitPrice,
    decimal? RegionMultiplier
);

public enum PriceMeter
{
    Input,
    Output,
    CacheRead,
    CacheWrite5m,
    CacheWrite1h,
    Reasoning,
}
