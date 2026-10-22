using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using Microsoft.EntityFrameworkCore;

namespace AiObservatory.Data.Audit;

public sealed partial class AuditStore
{
    // Shared by every scoped store/host in this process, not by validator or DI scope.
    // One admitted candidate holds at most MaxCaptureBytes (at most 512 MiB);
    // parser/copy working memory is additional. Other processes have their own admission bound
    // and still arbitrate publication through the database evidence fence.
    private static readonly SemaphoreSlim SealAdmission = new(1, 1);

    public async Task<Guid> SealCaptureAsync(
        CaptureLease lease,
        byte[] manifest,
        string idempotencyKey,
        CancellationToken ct
    )
    {
        ValidateKey(idempotencyKey);
        Require(manifest.Length <= 1024 * 1024, "manifest_size_limit", 413);
        // Always acquire admission before the evidence lock and before copying/loading bytes.
        // Waiting callers hold neither a transaction nor a detached full capture candidate.
        await SealAdmission.WaitAsync(ct);
        try
        {
            return await SealAdmittedCaptureAsync(lease, manifest, idempotencyKey, ct);
        }
        finally
        {
            SealAdmission.Release();
        }
    }

    private async Task<Guid> SealAdmittedCaptureAsync(
        CaptureLease lease,
        byte[] manifest,
        string idempotencyKey,
        CancellationToken ct
    )
    {
        var original = manifest.ToArray();
        var parsed = AuditCanonicalJson.Deserialize<CaptureManifest>(original);
        Require(original.AsSpan().SequenceEqual(AuditCanonicalJson.Serialize(parsed)), "noncanonical_manifest", 400);
        var candidate = await Transaction(
            async () =>
            {
                var request = await RequireCaptureLease(lease, allowSealed: true, ct);
                var existing = await ExistingSeal(lease.RequestId, original, idempotencyKey, ct);
                if (existing != null)
                {
                    return new SealCandidate(request.Revision, [], existing);
                }
                Require(
                    parsed.RequestId == request.Id
                        && parsed.MachineId == request.MachineId
                        && AuditCanonicalJson.Serialize(parsed.Scope).AsSpan().SequenceEqual(request.Scope),
                    "manifest_scope_mismatch",
                    409
                );
                Require(request.UploadedBytes + original.Length <= _options.MaxCaptureBytes, "capture_size_limit", 413);
                var chunks = await _db.Set<AuditEvidenceChunk>()
                    .AsNoTracking()
                    .Where(c => c.RequestId == request.Id)
                    .OrderBy(c => c.Index)
                    .Select(c => new SealChunkMetadata(c.Index, c.Sha256, c.Bytes.Length))
                    .ToArrayAsync(ct);
                Require(
                    chunks.Length == parsed.ChunkHashes.Count && request.RecordCount == parsed.RecordCount,
                    "capture_count_mismatch",
                    409
                );
                Require(
                    chunks.Sum(c => (long)c.Length) + original.Length <= _options.MaxCaptureBytes,
                    "capture_size_limit",
                    413
                );
                return new SealCandidate(request.Revision, chunks, null);
            },
            ct
        );
        if (candidate.ExistingId is { } retained)
        {
            return retained;
        }

        // This one statement reads request revision and bytes from the same PostgreSQL snapshot.
        // A reset/append before that snapshot returns no bytes, rather than loading a new generation
        // under the old candidate's size budget. No evidence transaction spans the transfer.
        var chunks = await _db.Set<AuditEvidenceChunk>()
            .AsNoTracking()
            .Where(c =>
                c.RequestId == lease.RequestId
                && _db.Set<AuditCaptureRequest>().Any(r => r.Id == lease.RequestId && r.Revision == candidate.Revision)
            )
            .OrderBy(c => c.Index)
            .Select(c => new AuditSealChunk(c.Index, c.Sha256, c.Bytes))
            .ToArrayAsync(ct);
        Require(
            chunks
                .Select(c => new SealChunkMetadata(c.Index, c.Sha256, c.Bytes.Length))
                .SequenceEqual(candidate.Chunks),
            "capture_generation_changed",
            409
        );
        // No transaction or evidence lock spans hashing, parsing, canonicalization or membership checks.
        await _captureValidator.ValidateAsync(parsed, chunks, ct);
        ct.ThrowIfCancellationRequested();
        var manifestHash = AuditCanonicalJson.ContentHash(original);
        return await Transaction(
            async () =>
            {
                // Transaction detaches audit entities before acquiring the lock. Credential and request
                // are freshly read here, including time, token and revocation independent of revision.
                var request = await RequireCaptureLease(lease, allowSealed: true, ct);
                var existing = await ExistingSeal(lease.RequestId, original, idempotencyKey, ct);
                if (existing != null)
                {
                    return existing.Value;
                }
                Require(request.Revision == candidate.Revision, "capture_generation_changed", 409);
                var generation = await _db.Set<AuditEvidenceChunk>()
                    .AsNoTracking()
                    .Where(c => c.RequestId == request.Id)
                    .OrderBy(c => c.Index)
                    .Select(c => new SealChunkMetadata(c.Index, c.Sha256, c.Bytes.Length))
                    .ToArrayAsync(ct);
                Require(generation.SequenceEqual(candidate.Chunks), "capture_generation_changed", 409);
                var now = _clock.GetCurrentInstant();
                Require(request.Deadline > now && request.LeaseExpiresAt > now, "capture_lease_expired", 410);
                var retention = await Retention(ct);
                _db.Add(
                    new AuditEvidenceCapture
                    {
                        Id = parsed.CaptureId,
                        RequestId = request.Id,
                        MachineId = request.MachineId,
                        Generation = parsed.Generation,
                        Manifest = original,
                        ManifestHash = manifestHash,
                        SealKey = idempotencyKey,
                        SealedAt = now,
                        ExpiresAt = Expiry(now, retention.EvidenceDays),
                    }
                );
                request.State = "sealed";
                request.Revision++;
                await _db.SaveChangesAsync(ct);
                return parsed.CaptureId;
            },
            ct
        );
    }

    private async Task<Guid?> ExistingSeal(Guid requestId, byte[] manifest, string key, CancellationToken ct)
    {
        var existing = await _db.Set<AuditEvidenceCapture>()
            .AsNoTracking()
            .SingleOrDefaultAsync(c => c.RequestId == requestId, ct);
        if (existing == null)
        {
            return null;
        }
        Require(
            existing.DeletedAt == null && existing.SealKey == key && existing.Manifest.AsSpan().SequenceEqual(manifest),
            "seal_retry_conflict",
            409
        );
        return existing.Id;
    }

    private sealed record SealChunkMetadata(int Index, string Sha256, int Length);

    private sealed record SealCandidate(long Revision, SealChunkMetadata[] Chunks, Guid? ExistingId);
}
