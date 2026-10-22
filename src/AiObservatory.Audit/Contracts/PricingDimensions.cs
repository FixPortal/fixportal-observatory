using System.Text.Json.Serialization;

namespace AiObservatory.Audit.Contracts;

public sealed record PricingDimensions(ServiceTier Tier, bool Batch, string? Region, long? ContextTokens)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Processing { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Context { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Service { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Sku { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Modality { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CacheLane { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? HighSpeed { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LongContext { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ContextThreshold { get; init; }
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
