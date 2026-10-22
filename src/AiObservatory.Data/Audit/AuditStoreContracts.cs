using AiObservatory.Audit.Contracts;
using NodaTime;

namespace AiObservatory.Data.Audit;

public sealed record RunLease(Guid RunId, Guid WorkerId, long Revision, Instant ExpiresAt);

public sealed record CaptureLease(
    Guid RequestId,
    Guid MachineId,
    Guid CredentialId,
    Guid Token,
    Instant ExpiresAt,
    AuditScope Scope
);

public sealed record MachineRegistration(Guid MachineId, Guid CredentialId);

public sealed record AuditCredentialView(Guid MachineId, Guid CredentialId, byte[] SecretHash, bool Revoked);

public sealed record AuditRunView(
    Guid Id,
    string State,
    long Revision,
    int Attempts,
    string? DiagnosticCode,
    Instant? EvidenceExpiresAt,
    Instant? SummaryExpiresAt,
    byte[]? Result
)
{
    public byte[]? SanitizedSummary { get; init; }
    public string? OriginalResultHash { get; init; }
    public string Stage { get; init; } = "queued";
    public Instant CreatedAt { get; init; }
    public Instant? CompletedAt { get; init; }
    public Instant? ServerCutoff { get; init; }
    public byte[] Scope { get; init; } = [];
    public Guid? PredecessorRunId { get; init; }
    public Guid? EvaluationOfRunId { get; init; }
}

public sealed record AuditCaptureDiscovery(IReadOnlyList<AvailableAuditCapture> Captures, bool ChoicesTruncated);

public sealed record AvailableAuditCapture(Guid Id, Guid MachineId, byte[] Manifest, Instant? ExpiresAt);

public sealed record RetainedCapture(byte[] Manifest, IReadOnlyList<byte[]> Chunks, Instant? ExpiresAt);

public sealed record RetainedSnapshot(byte[] Bytes, string Sha256, string InputContentDigest, Instant? ExpiresAt)
{
    public Guid SnapshotId { get; init; }
    public long RunRevision { get; init; }
}

public sealed record SnapshotMaterialization(AuditScope Scope, IReadOnlyList<RetainedCapture> Captures)
{
    public bool PartialEvidenceAcknowledged { get; init; }
}

public sealed class AuditStoreException(string code, int statusCode) : Exception(code)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

/// <summary>Detached bytes and the revision/deadline fence observed together under EvidenceLockKey.</summary>
public sealed record AuditExportMaterialization(
    Guid RunId,
    long RunRevision,
    byte[] Result,
    string ResultHash,
    RetainedSnapshot Snapshot,
    IReadOnlyList<RetainedCapture> Captures,
    Instant MaterializedAt,
    Instant? EvidenceExpiresAt
);

public sealed record ExpiryCounts(int Captures, int Runs, int Uploads);

public sealed record AuditRetentionDto(int? EvidenceDays, int? SummaryDays);

public sealed record DeletionImpact(Guid CaptureId, string ImpactDigest, IReadOnlyList<Guid> AffectedRunIds);

public sealed record DeleteResult(bool Deleted);

public sealed record AuditExportLease(Guid Id, Instant ExpiresAt);
