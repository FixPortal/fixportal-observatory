namespace AiObservatory.Audit.Contracts;

public sealed record FrozenSourceStatus(
    string SourceId,
    bool IsConfigured,
    bool? IsAvailable,
    long ExpectedRefreshIntervalSeconds,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LatestObservationAt,
    DateOnly? PendingFromDate,
    long ConsecutiveFailureCount,
    string? DiagnosticCode
);
