using NodaTime;

namespace AiObservatory.Ingest.Services.Anthropic;

public sealed record ClaudeCodeUsageRecord(
    LocalDate Date,
    string ActorType,
    string ActorIdentifier,
    string OrganizationId,
    string CustomerType,
    string? SubscriptionType,
    bool IsRemote,
    string TerminalType,
    string Model,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    decimal? EstimatedCostMinor,
    string? Currency,
    string RawJson
);
