using Microsoft.EntityFrameworkCore;

namespace AiObservatory.Data.Audit;

public sealed partial class AuditStore
{
    /// <summary>One lock-protected materialization. Parsing, hashes, verifier, ZIP and response streaming happen after commit.</summary>
    public Task<AuditExportMaterialization> MaterializeExportAsync(Guid runId, CancellationToken ct) =>
        Transaction(
            async () =>
            {
                var now = _clock.GetCurrentInstant();
                Require(await ExportByteCount(runId, ct) <= _options.MaxSnapshotBytes, "bundle_size_limit", 413);
                var run = await _db.Set<AuditRun>()
                    .AsNoTracking()
                    .SingleOrDefaultAsync(r => r.Id == runId && r.InstanceId == _instanceId, ct);
                Require(run != null, "run_not_found", 404);
                Require(run.Result != null && run.ResultHash != null, "report_unavailable", 409);
                Require(run.SummaryExpiresAt == null || run.SummaryExpiresAt > now, "report_expired", 410);
                Require(
                    (run.EvidenceExpiresAt == null || run.EvidenceExpiresAt > now)
                        && await EvidenceAvailable(runId, now, ct),
                    "evidence_expired",
                    410
                );
                var snapshot = await _db.Set<AuditServerSnapshot>()
                    .AsNoTracking()
                    .SingleOrDefaultAsync(s => s.RunId == runId, ct);
                Require(snapshot != null, "evidence_unavailable", 410);
                long total = checked(snapshot.Bytes.LongLength + run.Result.LongLength);
                Require(total <= _options.MaxSnapshotBytes, "bundle_size_limit", 413);
                var ids = await _db.Set<AuditRunCapture>()
                    .Where(r => r.RunId == runId)
                    .Select(r => r.CaptureId)
                    .Order()
                    .ToArrayAsync(ct);
                var captures = new List<RetainedCapture>();
                foreach (var id in ids)
                {
                    var capture = await _db.Set<AuditEvidenceCapture>()
                        .AsNoTracking()
                        .SingleOrDefaultAsync(c => c.Id == id, ct);
                    Require(
                        capture != null
                            && capture.DeletedAt == null
                            && (capture.ExpiresAt == null || capture.ExpiresAt > now),
                        "evidence_unavailable",
                        410
                    );
                    var length = await _db.Set<AuditEvidenceChunk>()
                        .Where(c => c.RequestId == capture.RequestId)
                        .SumAsync(c => (long)c.Bytes.Length, ct);
                    total = checked(total + capture.Manifest.LongLength + length);
                    Require(total <= _options.MaxSnapshotBytes, "bundle_size_limit", 413);
                    var chunks = await _db.Set<AuditEvidenceChunk>()
                        .AsNoTracking()
                        .Where(c => c.RequestId == capture.RequestId)
                        .OrderBy(c => c.Index)
                        .Select(c => c.Bytes)
                        .ToArrayAsync(ct);
                    captures.Add(new(capture.Manifest, chunks, capture.ExpiresAt));
                }
                return new AuditExportMaterialization(
                    runId,
                    run.Revision,
                    run.Result,
                    run.ResultHash,
                    new(snapshot.Bytes, snapshot.Sha256, snapshot.InputContentDigest, snapshot.ExpiresAt)
                    {
                        SnapshotId = snapshot.Id,
                        RunRevision = run.Revision,
                    },
                    captures,
                    now,
                    run.EvidenceExpiresAt
                );
            },
            ct
        );
}
