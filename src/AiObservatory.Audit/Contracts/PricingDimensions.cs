namespace AiObservatory.Audit.Contracts;

public sealed record PricingDimensions(ServiceTier Tier, bool Batch, string? Region, long? ContextTokens)
{
    public string? SourceServiceTier { get; init; }
    public string? Speed { get; init; }
    public string? InferenceGeo { get; init; }
}

public enum ServiceTier
{
    Unknown,
    Standard,
    Priority,
    Flex,
    Fast,
}
