using NodaTime;

namespace AiObservatory.Ingest.Services.Anthropic;

public sealed record AnthropicCostRecord(
    Instant BucketStart,
    Instant BucketEnd,
    decimal AmountFractionalCents,
    string Currency,
    string? WorkspaceId,
    string? Description,
    string? CostType,
    string? Model,
    string? ContextWindow,
    string? InferenceGeo,
    string? ServiceTier,
    string? TokenType,
    string RawJson
);
