namespace AiObservatory.Audit.Contracts;

public sealed record PricingDimensions(ServiceTier Tier, bool Batch, string? Region, long? ContextTokens);

public enum ServiceTier
{
    Unknown,
    Standard,
    Priority,
    Flex,
    Fast,
}
