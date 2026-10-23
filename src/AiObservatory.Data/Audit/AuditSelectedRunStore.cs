using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace AiObservatory.Data.Audit;

public sealed record SelectedRunBinding(Guid RunId, string ScopeDigest, long ExpectedRevision);

public sealed record SelectedRunAdmission(string Status, RunLease? Lease = null);

public sealed partial class AuditStore
{
    public Task<SelectedRunAdmission> TryAdmitSelectedRunAsync(
        SelectedRunBinding binding,
        Guid workerId,
        Instant admissionExpiresAt,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();
        Require(binding.RunId != Guid.Empty && workerId != Guid.Empty, "selected_binding_conflict", 409);
        // Reserve admission plus one owned completion/failure increment. This is
        // fresh-attempt authority, not permission for later ordinary queue recovery.
        Require(
            binding.ExpectedRevision >= 0
                && binding.ExpectedRevision < long.MaxValue - 1
                && binding.ScopeDigest is { Length: 64 }
                && binding.ScopeDigest.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'),
            "selected_binding_conflict",
            409
        );
        // The generic transaction detaches audit state. Refuse dirty state before it can
        // discard caller changes or SaveChanges can flush unrelated scoped work.
        Require(
            !_db
                .ChangeTracker.Entries()
                .Any(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted),
            "selected_dirty_context",
            409
        );
        return Transaction(
            async () =>
            {
                // Admission expiry must be checked after waiting for the evidence lock.
                var now = _clock.GetCurrentInstant();
                Require(now < admissionExpiresAt, "selected_admission_expired", 409);
                var run = await _db.Set<AuditRun>()
                    .SingleOrDefaultAsync(r => r.Id == binding.RunId && r.InstanceId == _instanceId, ct);
                Require(run != null, "selected_binding_conflict", 409);
                Require(!run.PartialEvidenceAcknowledged, "selected_partial_not_supported", 409);
                RequireSelectedState(run, binding, now);
                var scope = AuditCanonicalJson.Deserialize<AuditScope>(run.Scope);
                Require(
                    AuditCanonicalJson.ContentHash(ValidateScope(scope)) == binding.ScopeDigest,
                    "selected_binding_conflict",
                    409
                );
                Require(await EvidenceAvailable(run.Id, now, ct), "selected_evidence_unavailable", 409);

                var links = await _db.Set<AuditRunCapture>()
                    .Where(l => l.RunId == run.Id)
                    .Select(l => l.CaptureId)
                    .ToArrayAsync(ct);
                var prior = await _db.Set<AuditEvidenceCapture>().Where(c => links.Contains(c.Id)).ToArrayAsync(ct);
                Require(prior.Length == links.Length, "selected_evidence_unavailable", 409);
                var requests = await _db.Set<AuditCaptureRequest>()
                    .Where(r => run.CaptureRequestIds.Contains(r.Id))
                    .ToArrayAsync(ct);
                ValidateSelectedRequests(run, scope, requests, now);
                var collected = await _db.Set<AuditEvidenceCapture>()
                    .Where(c => run.CaptureRequestIds.Contains(c.RequestId))
                    .ToArrayAsync(ct);
                Require(collected.Length <= 200, "selected_capture_conflict", 409);
                var captures = prior.Concat(collected).DistinctBy(c => c.Id).ToArray();
                ValidateCaptureSelection(captures, scope);
                await ValidateSelectedCaptures(captures, now, ct);
                if (!SelectedCollectionReady(run, scope, requests, collected))
                {
                    return new SelectedRunAdmission("pending");
                }
                if (
                    await _db.Set<AuditRun>()
                        .CountAsync(
                            r => r.InstanceId == _instanceId && r.State == "executing" && r.LeaseExpiresAt > now,
                            ct
                        ) >= _options.MaxExecutingRuns
                )
                {
                    return new SelectedRunAdmission("busy");
                }
                var snapshotExpiry = await _db.Set<AuditServerSnapshot>()
                    .Where(s => s.RunId == run.Id)
                    .Select(s => s.ExpiresAt)
                    .SingleOrDefaultAsync(ct);
                // Metadata/capacity reads can outlive admission authority. Recheck every
                // temporal fence after the last read, immediately before selected writes.
                now = _clock.GetCurrentInstant();
                RequireSelectedAdmissionTime(run, captures, requests, snapshotExpiry, admissionExpiresAt, now);
                foreach (var capture in collected.Where(c => !links.Contains(c.Id)))
                {
                    _db.Add(new AuditRunCapture { RunId = run.Id, CaptureId = capture.Id });
                    run.EvidenceExpiresAt = MinExpiry(run.EvidenceExpiresAt, capture.ExpiresAt);
                }
                run.State = "executing";
                run.Stage = "collectingEvidence";
                run.DiagnosticCode = null;
                run.WorkerId = workerId;
                run.LeaseExpiresAt = now.Plus(Duration.FromTimeSpan(_options.RunLeaseDuration));
                run.Attempts = 1;
                run.Revision = checked(run.Revision + 1);
                await _db.SaveChangesAsync(ct);
                return new SelectedRunAdmission(
                    "admitted",
                    new RunLease(run.Id, workerId, run.Revision, run.LeaseExpiresAt.Value)
                );
            },
            ct
        );
    }

    private static void RequireSelectedAdmissionTime(
        AuditRun run,
        AuditEvidenceCapture[] captures,
        AuditCaptureRequest[] requests,
        Instant? snapshotExpiry,
        Instant admissionExpiresAt,
        Instant now
    )
    {
        Require(now < admissionExpiresAt, "selected_admission_expired", 409);
        Require(
            (run.EvidenceExpiresAt == null || run.EvidenceExpiresAt > now)
                && (run.SummaryExpiresAt == null || run.SummaryExpiresAt > now)
                && (snapshotExpiry == null || snapshotExpiry > now)
                && captures.All(c => c.ExpiresAt == null || c.ExpiresAt > now),
            "selected_evidence_unavailable",
            409
        );
        Require(requests.All(r => r.State == "sealed" || r.Deadline > now), "selected_capture_conflict", 409);
    }

    private static void RequireSelectedState(AuditRun run, SelectedRunBinding binding, Instant now)
    {
        Require(
            run.Revision == binding.ExpectedRevision
                && run.Attempts == 0
                && run.State is "queued" or "collecting"
                && run.WorkerId == null
                && run.LeaseExpiresAt == null,
            "selected_binding_conflict",
            409
        );
        Require(
            (run.EvidenceExpiresAt == null || run.EvidenceExpiresAt > now)
                && (run.SummaryExpiresAt == null || run.SummaryExpiresAt > now),
            "selected_evidence_unavailable",
            409
        );
    }

    private static void ValidateSelectedRequests(
        AuditRun run,
        AuditScope scope,
        AuditCaptureRequest[] requests,
        Instant now
    )
    {
        Require(
            requests.Length == run.CaptureRequestIds.Length && requests.Length <= 32,
            "selected_capture_conflict",
            409
        );
        foreach (var request in requests)
        {
            var subset = scope with { Sources = scope.Sources.Where(s => s.MachineId == request.MachineId).ToArray() };
            Require(
                subset.Sources.Count > 0
                    && request.Scope.AsSpan().SequenceEqual(AuditCanonicalJson.Serialize(subset))
                    && (
                        request.State == "sealed" || request.Deadline > now && request.State is "pending" or "uploading"
                    ),
                "selected_capture_conflict",
                409
            );
        }
    }

    private static bool SelectedCollectionReady(
        AuditRun run,
        AuditScope scope,
        AuditCaptureRequest[] requests,
        AuditEvidenceCapture[] collected
    )
    {
        var ready = requests.All(r => r.State == "sealed" && collected.Count(c => c.RequestId == r.Id) == 1);
        if (run.State == "queued")
        {
            Require(ready, "selected_capture_conflict", 409);
            return true;
        }
        Require(
            requests.Length > 0
                && requests.Select(r => r.MachineId).Distinct().Count() == requests.Length
                && scope
                    .Sources.Select(s => s.MachineId)
                    .Distinct()
                    .Order()
                    .SequenceEqual(requests.Select(r => r.MachineId).Order()),
            "selected_capture_conflict",
            409
        );
        return ready;
    }

    private async Task ValidateSelectedCaptures(AuditEvidenceCapture[] captures, Instant now, CancellationToken ct)
    {
        var requestIds = captures.Select(c => c.RequestId).Distinct().ToArray();
        var requests = await _db.Set<AuditCaptureRequest>()
            .Where(r => requestIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, ct);
        foreach (var capture in captures)
        {
            Require(
                capture.DeletedAt == null && (capture.ExpiresAt == null || capture.ExpiresAt > now),
                "selected_evidence_unavailable",
                409
            );
            requests.TryGetValue(capture.RequestId, out var request);
            var manifest = AuditCanonicalJson.Deserialize<CaptureManifest>(capture.Manifest);
            Require(
                request != null
                    && request.State == "sealed"
                    && request.MachineId == capture.MachineId
                    && manifest.CaptureId == capture.Id
                    && manifest.RequestId == capture.RequestId
                    && manifest.MachineId == capture.MachineId
                    && manifest.Generation == capture.Generation
                    && AuditCanonicalJson.ContentHash(capture.Manifest) == capture.ManifestHash
                    && request.Scope.AsSpan().SequenceEqual(AuditCanonicalJson.Serialize(manifest.Scope)),
                "selected_capture_conflict",
                409
            );
        }
    }
}
