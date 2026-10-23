using System.Data;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using Microsoft.EntityFrameworkCore;

namespace AiObservatory.Data.Audit;

public sealed partial class AuditStore
{
    // Schema1 has at most32 selections. Even allowing six JSON bytes per UTF-16
    // unit, each256-unit SourceId plus GUID/keys fits2000 bytes. The root keys,
    // dates, boolean, two provenance GUIDs and all six check names fit1536 bytes:
    // 32*2000+1536=65536. This deliberately exceeds the current ASCII identifier
    // grammar; a corrupt stored blob must still be bounded before materialization.
    private const int SelectedMetadataScopeBytes = 64 * 1024;

    public async Task<SelectedRunMetadata?> ReadSelectedRunMetadataAsync(SelectedRunBinding binding, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RequireSelectedMetadataBinding(binding);
        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await _db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
        var run = await _db.Set<AuditRun>().AsNoTracking()
            .Where(r => r.Id == binding.RunId && r.InstanceId == _instanceId)
            .Select(r => new
            {
                r.Id,
                r.Revision,
                r.State,
                r.Stage,
                r.Attempts,
                r.EvidenceExpiresAt,
                r.SummaryExpiresAt,
                r.DiagnosticCode,
                HasResult = r.Result != null,
                Scope = r.Scope.Length <= SelectedMetadataScopeBytes ? r.Scope : null,
                RequestIds = r.CaptureRequestIds.Length <= 32 ? r.CaptureRequestIds : null,
            }).SingleOrDefaultAsync(ct);
        if (run == null)
        {
            await transaction.CommitAsync(ct);
            return null;
        }
        Require(run.RequestIds != null && run.RequestIds.Distinct().Count() == run.RequestIds.Length,
            "selected_metadata_unavailable", 503);
        var scope = SelectedMetadataScope(run.Scope);
        var digest = AuditCanonicalJson.ContentHash(AuditCanonicalJson.Serialize(scope));
        Require(digest == binding.ScopeDigest, "selected_binding_conflict", 409);
        var requests = await SelectedMetadataRequests(run.RequestIds, scope, ct);
        var captures = await _db.Set<AuditRunCapture>().AsNoTracking()
            .Where(l => l.RunId == run.Id).OrderBy(l => l.CaptureId)
            .Select(l => l.CaptureId).Take(201).ToArrayAsync(ct);
        Require(captures.Length <= 200, "selected_metadata_unavailable", 503);
        var now = _clock.GetCurrentInstant();
        var evidenceUnavailable = await _db.Set<AuditEvidenceCapture>().AsNoTracking()
            .Where(c => captures.Contains(c.Id))
            .AnyAsync(c => c.DeletedAt != null || c.ExpiresAt != null && c.ExpiresAt <= now, ct);
        var snapshotAvailable = await _db.Set<AuditServerSnapshot>().AsNoTracking()
            .AnyAsync(s => s.RunId == run.Id && (s.ExpiresAt == null || s.ExpiresAt > now), ct);
        var available = run.HasResult && snapshotAvailable && !evidenceUnavailable
            && (run.EvidenceExpiresAt == null || run.EvidenceExpiresAt > now)
            && (run.SummaryExpiresAt == null || run.SummaryExpiresAt > now);
        var metadata = new SelectedRunMetadata(run.Id, digest, run.Revision, run.State, run.Stage, run.Attempts,
            run.RequestIds, requests, captures, run.EvidenceExpiresAt, run.SummaryExpiresAt, run.DiagnosticCode, available);
        await transaction.CommitAsync(ct);
        return metadata;
    }

    private static void RequireSelectedMetadataBinding(SelectedRunBinding binding) =>
        Require(binding.RunId != Guid.Empty && binding.ExpectedRevision >= 0
            && binding.ScopeDigest is { Length: 64 }
            && binding.ScopeDigest.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'),
            "selected_binding_conflict", 409);

    private async Task<IReadOnlyList<SelectedRunRequestMetadata>> SelectedMetadataRequests(
        Guid[] ids, AuditScope scope, CancellationToken ct)
    {
        var rows = await _db.Set<AuditCaptureRequest>().AsNoTracking().Where(r => ids.Contains(r.Id))
            .OrderBy(r => r.Id).Select(r => new
            {
                r.Id,
                r.MachineId,
                r.Deadline,
                r.State,
                Scope = r.Scope.Length <= SelectedMetadataScopeBytes ? r.Scope : null,
            }).ToArrayAsync(ct);
        Require(rows.Length == ids.Length && rows.Select(r => r.MachineId).Distinct().Count() == rows.Length,
            "selected_metadata_unavailable", 503);
        var result = new List<SelectedRunRequestMetadata>(rows.Length);
        foreach (var row in rows)
        {
            var subset = scope with { Sources = scope.Sources.Where(s => s.MachineId == row.MachineId).ToArray() };
            Require(subset.Sources.Count > 0, "selected_metadata_unavailable", 503);
            var canonical = AuditCanonicalJson.Serialize(SelectedMetadataScope(row.Scope));
            Require(canonical.AsSpan().SequenceEqual(AuditCanonicalJson.Serialize(subset)), "selected_metadata_unavailable", 503);
            result.Add(new(row.Id, row.MachineId, AuditCanonicalJson.ContentHash(canonical), row.Deadline, row.State));
        }
        return result;
    }

    private static AuditScope SelectedMetadataScope(byte[]? bytes)
    {
        Require(bytes != null, "selected_metadata_unavailable", 503);
        try
        {
            var scope = AuditCanonicalJson.Deserialize<AuditScope>(bytes);
            Require(scope.Sources.Count <= 32, "selected_metadata_unavailable", 503);
            return scope;
        }
        catch (AuditValidationException)
        {
            throw new AuditStoreException("selected_metadata_unavailable", 503);
        }
    }
}
