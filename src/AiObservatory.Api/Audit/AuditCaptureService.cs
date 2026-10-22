using AiObservatory.Audit.Serialization;
using AiObservatory.Data.Audit;

namespace AiObservatory.Api.Audit;

public sealed class AuditCaptureService(AuditStore store)
{
    public async Task<SealCaptureResult> SealAsync(
        Guid requestId,
        SealCaptureRequest body,
        MachineLease lease,
        CancellationToken ct
    )
    {
        if (body.SchemaVersion != 1)
        {
            throw new AuditStoreException("invalid_audit_request", 400);
        }
        var bytes = AuditCanonicalJson.Serialize(body.Manifest);
        if (AuditCanonicalJson.ContentHash(bytes) != body.Sha256)
        {
            throw new AuditStoreException("manifest_hash_mismatch", 400);
        }
        var resolved = await store.ResolveCaptureLeaseAsync(
            requestId,
            lease.MachineId,
            lease.CredentialId,
            lease.Token,
            ct
        );
        return new(await store.SealCaptureAsync(resolved, bytes, body.IdempotencyKey, ct));
    }
}
