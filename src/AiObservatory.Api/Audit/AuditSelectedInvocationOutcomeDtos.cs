namespace AiObservatory.Api.Audit;

public sealed record SelectedCompactRunState(
    long Revision,
    string State,
    string Stage,
    int Attempts,
    string? DiagnosticCode,
    bool ResultAvailable,
    DateTimeOffset? EvidenceExpiresAt,
    DateTimeOffset? SummaryExpiresAt
);

public sealed record SelectedInvocationOutcome(string Status, string Code, SelectedCompactRunState? Current = null);

public sealed record SelectedInvocationPostResponse(
    int SchemaVersion,
    string Status,
    string Code,
    Guid RunId,
    Guid InvocationId,
    string FullScopeDigest,
    long ExpectedRevision,
    SelectedCompactRunState? Current
);
