using NodaTime;

namespace AiObservatory.Ingest.Services.OpenAi;

public sealed record OpenAiCostRecord(
    Instant BucketStart,
    Instant BucketEnd,
    decimal Amount,
    string Currency,
    string? LineItem,
    string? ProjectId,
    decimal? Quantity,
    string? QuantityUnit,
    string RawJson
);
