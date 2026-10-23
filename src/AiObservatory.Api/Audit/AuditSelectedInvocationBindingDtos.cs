namespace AiObservatory.Api.Audit;

public sealed record SelectedInvocationBindingResponse(
    int SchemaVersion,
    Guid RunId,
    Guid InvocationId,
    string FullScopeDigest,
    long ExpectedRevision,
    long CurrentRevision,
    string State,
    string Stage,
    int Attempts,
    IReadOnlyList<Guid> CaptureRequestIds,
    IReadOnlyList<SelectedInvocationBindingRequest> Requests,
    IReadOnlyList<Guid> CaptureIds,
    DateTimeOffset? EvidenceExpiresAt,
    DateTimeOffset? SummaryExpiresAt,
    string? DiagnosticCode,
    bool ResultAvailable
);

public sealed record SelectedInvocationBindingRequest(
    Guid RequestId,
    Guid MachineId,
    string RequestScopeDigest,
    DateTimeOffset Deadline,
    string State
);
