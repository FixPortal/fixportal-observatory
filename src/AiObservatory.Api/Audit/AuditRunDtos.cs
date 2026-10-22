using AiObservatory.Audit.Contracts;

namespace AiObservatory.Api.Audit;

public sealed record CreateAuditRunRequest(AuditScope Scope, Guid[] CaptureIds, bool RequestFreshCapture);

public sealed record AuditReadinessDto(
    int SchemaVersion,
    IReadOnlyList<AuditSourceReadinessDto> Sources,
    int MaximumRunWindowDays,
    long MaximumSnapshotBytes
);

public sealed record AuditSourceReadinessDto(
    string SourceId,
    Guid? MachineId,
    Guid[] CaptureIds,
    string[] DiagnosticCodes,
    DateTimeOffset? LastSuccessAt
);
