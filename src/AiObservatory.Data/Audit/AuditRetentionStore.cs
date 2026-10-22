using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace AiObservatory.Data.Audit;

public sealed partial class AuditStore
{
    public const int MaxConcurrentExports = 2;
    public static readonly Duration ExportLeaseDuration = Duration.FromMinutes(2);
    private const int CleanupBatchSize = 100;

    private static byte[] SanitizedResult(AuditResult result)
    {
        static AuditCoverage SafeCoverage(AuditCoverage coverage) =>
            new(coverage.ComparedGroups, coverage.TotalGroups, []);
        return AuditCanonicalJson.Serialize(
            result with
            {
                Findings = [],
                Coverage = SafeCoverage(result.Coverage),
                Checks = result
                    .Checks.Select(c =>
                        c with
                        {
                            Findings = [],
                            Coverage = SafeCoverage(c.Coverage),
                            PricingTraces = null,
                        }
                    )
                    .ToArray(),
            }
        );
    }

    public async Task<AuditRunView> ReadReportAsync(Guid id, CancellationToken ct)
    {
        var run = await GetRunAsync(id, ct);
        if (run != null)
        {
            return run;
        }
        var expired = await _db.Set<AuditRun>()
            .AnyAsync(
                r => r.Id == id && r.InstanceId == _instanceId && r.SummaryExpiresAt <= _clock.GetCurrentInstant(),
                ct
            );
        throw new AuditStoreException(expired ? "report_expired" : "run_not_found", expired ? 410 : 404);
    }

    public Task<AuditRetentionDto> GetRetentionAsync(CancellationToken ct) =>
        Transaction(
            async () =>
            {
                var settings = await Retention(ct);
                return new AuditRetentionDto(settings.EvidenceDays, settings.SummaryDays);
            },
            ct
        );

    // Includes sealed captures assigned to a collecting run before its explicit run/capture link exists.
    private IQueryable<AuditRun> DependentRuns(Guid captureId, Guid requestId) =>
        _db.Set<AuditRun>()
            .Where(r =>
                r.CaptureRequestIds.Contains(requestId)
                || _db.Set<AuditRunCapture>().Any(l => l.RunId == r.Id && l.CaptureId == captureId)
            );

    public Task<DeletionImpact> GetDeletionImpactAsync(Guid id, CancellationToken ct) =>
        Transaction(
            async () =>
            {
                var capture = await _db.Set<AuditEvidenceCapture>()
                    .AsNoTracking()
                    .SingleOrDefaultAsync(c => c.Id == id, ct);
                Require(capture != null, "capture_not_found", 404);
                var runs = await DependentRuns(id, capture.RequestId)
                    .OrderBy(r => r.Id)
                    .Select(r => r.Id)
                    .ToArrayAsync(ct);
                return new DeletionImpact(id, await Impact(id, ct), runs);
            },
            ct
        );

    public Task FenceCaptureDeletionAsync(Guid id, string digest, CancellationToken ct) =>
        Transaction(
            async () =>
            {
                Require(await Impact(id, ct) == digest, "deletion_impact_changed", 412);
                await FenceCapture(id, _clock.GetCurrentInstant(), "evidence_deleted", ct);
                await _db.SaveChangesAsync(ct);
                return true;
            },
            ct
        );

    private async Task FenceCapture(Guid id, Instant now, string diagnostic, CancellationToken ct)
    {
        var capture = await _db.Set<AuditEvidenceCapture>().SingleAsync(c => c.Id == id, ct);
        if (capture.DeletedAt != null)
        {
            return;
        }
        capture.DeletedAt = now;
        capture.Revision++;
        var runs = await DependentRuns(id, capture.RequestId).ToArrayAsync(ct);
        foreach (var run in runs)
        {
            await FenceRun(run, now, diagnostic, ct);
        }
    }

    private async Task FenceRun(AuditRun run, Instant now, string diagnostic, CancellationToken ct)
    {
        if (IsTerminal(run.State))
        {
            run.Revision++;
        }
        else
        {
            await Complete(run, "failed", diagnostic, now, ct);
        }
        run.DiagnosticCode = diagnostic;
        run.EvidenceExpiresAt = MinExpiry(run.EvidenceExpiresAt, now);
        run.ServerCutoff ??= await _db.Set<AuditServerSnapshot>()
            .Where(s => s.RunId == run.Id)
            .Select(s => (Instant?)s.FrozenAt)
            .SingleOrDefaultAsync(ct);
        // Summary is separately generated at publication; original hash remains an identity, never a hash of this summary.
        run.Result = null;
        await _db.Set<AuditFindingRecord>().Where(f => f.RunId == run.Id).ExecuteDeleteAsync(ct);
        await _db.Set<AuditExportReadLease>()
            .Where(l => l.RunId == run.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.Cancelled, true), ct);
    }

    public Task<bool> PurgeUnavailableCaptureAsync(Guid id, CancellationToken ct) =>
        Transaction(
            async () =>
            {
                var capture = await _db.Set<AuditEvidenceCapture>().SingleOrDefaultAsync(c => c.Id == id, ct);
                Require(capture != null && capture.DeletedAt != null, "capture_not_deleted", 409);
                var runIds = DependentRuns(id, capture.RequestId).Select(r => r.Id);
                var now = _clock.GetCurrentInstant();
                if (
                    await _db.Set<AuditExportReadLease>()
                        .AnyAsync(l => runIds.Contains(l.RunId) && l.ExpiresAt > now, ct)
                )
                {
                    return false;
                }
                capture.Manifest = [];
                capture.Generation = "";
                await _db.Set<AuditEvidenceChunk>().Where(c => c.RequestId == capture.RequestId).ExecuteDeleteAsync(ct);
                await _db.Set<AuditServerSnapshot>().Where(s => runIds.Contains(s.RunId)).ExecuteDeleteAsync(ct);
                await _db.SaveChangesAsync(ct);
                return true;
            },
            ct
        );

    public Task<AuditExportLease> AcquireExportLeaseAsync(Guid id, CancellationToken ct) =>
        Transaction(
            async () =>
            {
                var now = _clock.GetCurrentInstant();
                var run = await _db.Set<AuditRun>()
                    .AsNoTracking()
                    .Where(r => r.Id == id && r.InstanceId == _instanceId)
                    .Select(r => new
                    {
                        r.SummaryExpiresAt,
                        r.EvidenceExpiresAt,
                        HasResult = r.Result != null,
                    })
                    .SingleOrDefaultAsync(ct);
                Require(run != null, "run_not_found", 404);
                Require(
                    (run.SummaryExpiresAt == null || run.SummaryExpiresAt > now)
                        && (run.EvidenceExpiresAt == null || run.EvidenceExpiresAt > now)
                        && await EvidenceAvailable(id, now, ct),
                    "evidence_expired",
                    410
                );
                Require(run.HasResult, "report_unavailable", 409);
                var active = _db.Set<AuditExportReadLease>()
                    .Where(l => l.InstanceId == _instanceId && l.ExpiresAt > now);
                Require(await active.CountAsync(ct) < MaxConcurrentExports, "export_capacity", 429);
                var bytes = await ExportByteCount(id, ct);
                Require(bytes <= _options.MaxSnapshotBytes, "bundle_size_limit", 413);
                Require(
                    await active.SumAsync(l => l.ReservedBytes, ct) + bytes
                        <= (long)MaxConcurrentExports * _options.MaxSnapshotBytes,
                    "export_capacity",
                    429
                );
                var lease = new AuditExportReadLease
                {
                    Id = Guid.NewGuid(),
                    RunId = id,
                    InstanceId = _instanceId,
                    ExpiresAt = Min(
                        Instant.MaxValue - now < ExportLeaseDuration ? Instant.MaxValue : now.Plus(ExportLeaseDuration),
                        run.EvidenceExpiresAt ?? Instant.MaxValue
                    ),
                    ReservedBytes = bytes,
                };
                _db.Add(lease);
                await _db.SaveChangesAsync(ct);
                return new AuditExportLease(lease.Id, lease.ExpiresAt);
            },
            ct
        );

    private async Task<long> ExportByteCount(Guid runId, CancellationToken ct)
    {
        var ids = _db.Set<AuditRunCapture>().Where(l => l.RunId == runId).Select(l => l.CaptureId);
        var captures = _db.Set<AuditEvidenceCapture>().Where(c => ids.Contains(c.Id));
        var requests = captures.Select(c => c.RequestId);
        return checked(
            await _db.Set<AuditRun>()
                .Where(r => r.Id == runId)
                .SumAsync(r => (long)(r.Result == null ? 0 : r.Result.Length), ct)
            + await _db.Set<AuditServerSnapshot>().Where(s => s.RunId == runId).SumAsync(s => (long)s.Bytes.Length, ct)
            + await captures.SumAsync(c => (long)c.Manifest.Length, ct)
            + await _db.Set<AuditEvidenceChunk>()
                .Where(c => requests.Contains(c.RequestId))
                .SumAsync(c => (long)c.Bytes.Length, ct)
        );
    }

    public Task<bool> ExportLeaseValidAsync(Guid id, CancellationToken ct)
    {
        var now = _clock.GetCurrentInstant();
        return _db.Set<AuditExportReadLease>()
            .AsNoTracking()
            .AnyAsync(l => l.Id == id && !l.Cancelled && l.ExpiresAt > now, ct);
    }

    public Task ReleaseExportLeaseAsync(Guid id, CancellationToken ct) =>
        Transaction(
            async () =>
            {
                await _db.Set<AuditExportReadLease>().Where(l => l.Id == id).ExecuteDeleteAsync(ct);
                return true;
            },
            ct
        );

    public Task<ExpiryCounts> ExpireAsync(Instant now, CancellationToken ct) =>
        Transaction(
            async () =>
            {
                var captures = await _db.Set<AuditEvidenceCapture>()
                    .Where(c => c.DeletedAt == null && c.ExpiresAt <= now)
                    .OrderBy(c => c.ExpiresAt)
                    .Take(CleanupBatchSize)
                    .Select(c => c.Id)
                    .ToArrayAsync(ct);
                foreach (var id in captures)
                {
                    await FenceCapture(id, now, "evidence_expired", ct);
                }
                await ExpireRunDetails(now, ct);
                await _db.SaveChangesAsync(ct);
                await PurgeCaptureBatch(now, ct);
                var runIds = await _db.Set<AuditRun>()
                    .Where(r => r.SummaryExpiresAt <= now)
                    .OrderBy(r => r.SummaryExpiresAt)
                    .Take(CleanupBatchSize)
                    .Select(r => r.Id)
                    .ToArrayAsync(ct);
                foreach (var id in runIds)
                {
                    await DeleteRunRows(id, now, ct);
                }
                var uploads = await _db.Set<AuditCaptureRequest>()
                    .Where(r =>
                        (r.State == "uploading" || r.State == "pending")
                        && (r.Deadline <= now || r.CreatedAt <= now - Duration.FromHours(24))
                    )
                    .OrderBy(r => r.CreatedAt)
                    .Take(CleanupBatchSize)
                    .ToArrayAsync(ct);
                foreach (var upload in uploads)
                {
                    upload.State = "expired";
                    upload.LeaseToken = null;
                    upload.Revision++;
                    upload.UploadedBytes = 0;
                    upload.RecordCount = 0;
                    await _db.Set<AuditEvidenceChunk>().Where(c => c.RequestId == upload.Id).ExecuteDeleteAsync(ct);
                }
                await _db.Set<AuditExportReadLease>().Where(l => l.ExpiresAt <= now).ExecuteDeleteAsync(ct);
                await _db.SaveChangesAsync(ct);
                return new ExpiryCounts(captures.Length, runIds.Length, uploads.Length);
            },
            ct
        );

    private async Task ExpireRunDetails(Instant now, CancellationToken ct)
    {
        var details = await _db.Set<AuditRun>()
            .Where(r =>
                r.EvidenceExpiresAt <= now
                && (r.Result != null || _db.Set<AuditServerSnapshot>().Any(s => s.RunId == r.Id))
            )
            .OrderBy(r => r.EvidenceExpiresAt)
            .Take(CleanupBatchSize)
            .ToArrayAsync(ct);
        foreach (var run in details)
        {
            await FenceRun(run, now, "evidence_expired", ct);
            if (!await _db.Set<AuditExportReadLease>().AnyAsync(l => l.RunId == run.Id && l.ExpiresAt > now, ct))
            {
                await _db.Set<AuditServerSnapshot>().Where(s => s.RunId == run.Id).ExecuteDeleteAsync(ct);
            }
        }
    }

    private async Task PurgeCaptureBatch(Instant now, CancellationToken ct)
    {
        var pending = await _db.Set<AuditEvidenceCapture>()
            .Where(c => c.DeletedAt != null && c.Manifest.Length > 0)
            .OrderBy(c => c.DeletedAt)
            .Take(CleanupBatchSize)
            .ToArrayAsync(ct);
        foreach (var capture in pending)
        {
            var runs = DependentRuns(capture.Id, capture.RequestId).Select(r => r.Id);
            if (await _db.Set<AuditExportReadLease>().AnyAsync(l => runs.Contains(l.RunId) && l.ExpiresAt > now, ct))
            {
                continue;
            }
            capture.Manifest = [];
            capture.Generation = "";
            await _db.Set<AuditEvidenceChunk>().Where(c => c.RequestId == capture.RequestId).ExecuteDeleteAsync(ct);
        }
    }

    public Task<bool> DeleteRunAsync(Guid id, CancellationToken ct) =>
        Transaction(
            async () =>
            {
                var run = await _db.Set<AuditRun>()
                    .SingleOrDefaultAsync(r => r.Id == id && r.InstanceId == _instanceId, ct);
                Require(run != null, "run_not_found", 404);
                await FenceRun(run, _clock.GetCurrentInstant(), "run_deleted", ct);
                run.SummaryExpiresAt = _clock.GetCurrentInstant();
                await _db.SaveChangesAsync(ct);
                return await DeleteRunRows(id, _clock.GetCurrentInstant(), ct);
            },
            ct
        );

    private async Task<bool> DeleteRunRows(Guid id, Instant now, CancellationToken ct)
    {
        await _db.Set<AuditExportReadLease>()
            .Where(l => l.RunId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.Cancelled, true), ct);
        if (await _db.Set<AuditExportReadLease>().AnyAsync(l => l.RunId == id && l.ExpiresAt > now, ct))
        {
            return false;
        }
        var captures = await _db.Set<AuditEvidenceCapture>()
            .Where(c =>
                _db.Set<AuditRunCapture>().Any(l => l.RunId == id && l.CaptureId == c.Id)
                || _db.Set<AuditRun>().Any(r => r.Id == id && r.CaptureRequestIds.Contains(c.RequestId))
            )
            .ToArrayAsync(ct);
        await _db.Set<AuditRun>().Where(r => r.Id == id).ExecuteDeleteAsync(ct);
        foreach (var entry in _db.ChangeTracker.Entries<AuditRun>().Where(e => e.Entity.Id == id).ToArray())
        {
            entry.State = EntityState.Detached;
        }
        foreach (var capture in captures)
        {
            if (await DependentRuns(capture.Id, capture.RequestId).AnyAsync(ct))
            {
                continue;
            }
            await _db.Set<AuditEvidenceChunk>().Where(c => c.RequestId == capture.RequestId).ExecuteDeleteAsync(ct);
            await _db.Set<AuditEvidenceCapture>().Where(c => c.Id == capture.Id).ExecuteDeleteAsync(ct);
            _db.Entry(capture).State = EntityState.Detached;
        }
        return true;
    }
}
