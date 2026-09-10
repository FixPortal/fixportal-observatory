using NodaTime;

namespace AiObservatory.Ingest.Services.Anthropic;

public record AnthropicUsageRecord(
    Instant BucketStart,
    Instant BucketEnd,
    string? Model,
    string? ServiceTier,
    string? InferenceGeo,
    string? Speed,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWrite5mTokens,
    long CacheWrite1hTokens,
    string RawJson
);
