using NodaTime;

namespace AiObservatory.Ingest.Services.OpenAi;

// Provider payload properties are populated and consumed by JSON serialization.
// ReSharper disable NotAccessedPositionalProperty.Global

public sealed record OpenAiUsageRecord(
    Instant BucketStart,
    Instant BucketEnd,
    string? Model,
    bool? Batch,
    string? ServiceTier,
    string? Processing,
    long InputUncachedTokens,
    long InputCachedTokens,
    long InputCacheWriteTokens,
    long OutputTokens,
    long ModelRequests,
    string RawJson
);
