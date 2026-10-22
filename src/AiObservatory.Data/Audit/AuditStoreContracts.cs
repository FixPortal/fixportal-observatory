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
    public string Stage { get; init; } = "queued";
    public Instant CreatedAt { get; init; }
    public Instant? ServerCutoff { get; init; }
    public byte[] Scope { get; init; } = [];
    public Guid? PredecessorRunId { get; init; }
    public Guid? EvaluationOfRunId { get; init; }
}

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
