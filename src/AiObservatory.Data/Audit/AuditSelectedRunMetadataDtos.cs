using NodaTime;

namespace AiObservatory.Data.Audit;

/// <summary>Current bounded run facts, without invocation attribution or evidence payloads.</summary>
public sealed record SelectedRunMetadata(
    Guid RunId,
    string FullScopeDigest,
    long CurrentRevision,
    string State,
    string Stage,
    int Attempts,
    IReadOnlyList<Guid> CaptureRequestIds,
    IReadOnlyList<SelectedRunRequestMetadata> Requests,
    IReadOnlyList<Guid> CaptureIds,
    Instant? EvidenceExpiresAt,
    Instant? SummaryExpiresAt,
    string? DiagnosticCode,
    bool ResultAvailable
);

public sealed record SelectedRunRequestMetadata(
    Guid RequestId,
    Guid MachineId,
    string RequestScopeDigest,
    Instant Deadline,
    string State
);
