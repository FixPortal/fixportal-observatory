using System.Globalization;
using AiObservatory.Audit;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace AiObservatory.Data.Audit;

public sealed partial class AuditStore
{
    private async Task<Guid[]> CreateRunCaptureRequests(
        AuditScope scope,
        bool collecting,
        Instant now,
        CancellationToken ct
    )
    {
        if (!collecting)
        {
            return [];
        }
        var requests = new List<Guid>();
        foreach (var machine in scope.Sources.GroupBy(s => s.MachineId))
        {
            Require(await _db.Set<AuditMachine>().AnyAsync(m => m.Id == machine.Key, ct), "machine_not_found", 404);
            var pending = await _db.Set<AuditCaptureRequest>()
                .CountAsync(
                    r =>
                        r.MachineId == machine.Key
                        && r.Deadline > now
                        && (r.State == "pending" || r.State == "uploading"),
                    ct
                );
            Require(pending < _options.MaxOutstandingRequestsPerMachine, "capture_queue_full", 429);
            var request = new AuditCaptureRequest
            {
                Id = Guid.NewGuid(),
                MachineId = machine.Key,
                Scope = AuditCanonicalJson.Serialize(scope with { Sources = machine.ToArray() }),
                CreatedAt = now,
                Deadline = now.Plus(Duration.FromTimeSpan(_options.CaptureRequestDeadline)),
            };
            _db.Add(request);
            requests.Add(request.Id);
        }
        return requests.ToArray();
    }

    private static string ProbeRequestHash(AuditScope scope, FrozenPresentationProbe? probe, string hash)
    {
        if (probe == null)
        {
            return hash;
        }
        Require(scope.EvaluationOfRunId != null, "probe_requires_evaluation", 400);
        return AuditCanonicalJson.ContentHash([
            .. System.Text.Encoding.UTF8.GetBytes(hash),
            .. AuditCanonicalJson.Serialize(probe),
        ]);
    }

    private sealed record PreparedReplay(
        RetainedSnapshot Original,
        byte[] Scope,
        byte[] Bytes,
        string Sha256,
        string InputContentDigest
    );

    private async Task<PreparedReplay?> PrepareReplayAsync(
        AuditScope scope,
        FrozenPresentationProbe? probe,
        CancellationToken ct
    )
    {
        if (scope.EvaluationOfRunId is not { } id)
        {
            return null;
        }
        var original = await ReadSnapshotAsync(id, ct);
        if (original == null)
        {
            return null;
        }
        // The evidence transaction has ended. Parsing, canonicalization and hashing large
        // frozen inputs must never hold the shared evidence lock.
        var pinned = CompatibleReplayInput(original.Bytes);
        Require(
            AuditCanonicalJson.ContentHash(original.Bytes) == original.Sha256
                && AuditComputationInput.ContentDigest(pinned) == original.InputContentDigest,
            "retained_evidence_integrity",
            409
        );
        if (probe != null)
        {
            Require(
                probe.FrozenSnapshotHash == original.Sha256
                    && probe.SummaryInputDigest
                        == AiObservatory.Audit.Checks.PresentationTotalsCheck.SummaryInputDigest(pinned),
                "stale_presentation_probe",
                409
            );
        }
        var evaluation = pinned with
        {
            PresentationProbe = probe ?? pinned.PresentationProbe,
            VerifierVersion = AuditVerifierIdentity.VerifierVersion,
            RuleVersion = AuditVerifierIdentity.RuleVersion,
        };
        var bytes = AuditCanonicalJson.Serialize(evaluation);
        Require(bytes.LongLength <= _options.MaxSnapshotBytes, "snapshot_size_limit", 413);
        return new(
            original,
            AuditCanonicalJson.Serialize(AuditResultScope.FromScope(pinned.Scope)),
            bytes,
            AuditCanonicalJson.ContentHash(bytes),
            AuditComputationInput.ContentDigest(evaluation)
        );
    }

    private async Task PinReplay(AuditRun run, Guid[] ids, PreparedReplay? prepared, Instant now, CancellationToken ct)
    {
        if (run.EvaluationOfRunId is not { } originalId)
        {
            return;
        }
        Require(run.State == "queued", "replay_capture_conflict", 409);
        var revision = await _db.Set<AuditRun>()
            .AsNoTracking()
            .Where(r => r.Id == originalId && r.InstanceId == _instanceId)
            .Select(r => (long?)r.Revision)
            .SingleOrDefaultAsync(ct);
        Require(revision != null, "run_not_found", 404);
        Require(await EvidenceAvailable(originalId, now, ct), "replay_expired", 410);
        var snapshot = await _db.Set<AuditServerSnapshot>()
            .AsNoTracking()
            .Where(s => s.RunId == originalId)
            .Select(s => new
            {
                s.Id,
                s.Sha256,
                s.FrozenAt,
                s.ExpiresAt,
            })
            .SingleOrDefaultAsync(ct);
        Require(snapshot != null && prepared != null, "replay_unavailable", 410);
        Require(
            snapshot.Id == prepared.Original.SnapshotId
                && snapshot.Sha256 == prepared.Original.Sha256
                && revision == prepared.Original.RunRevision,
            "replay_input_changed",
            409
        );
        var originalIds = await _db.Set<AuditRunCapture>()
            .Where(r => r.RunId == originalId)
            .Select(r => r.CaptureId)
            .ToArrayAsync(ct);
        Require(ids.Order().SequenceEqual(originalIds.Order()), "replay_capture_conflict", 409);
        Require(
            prepared
                .Scope.AsSpan()
                .SequenceEqual(
                    AuditCanonicalJson.Serialize(
                        AuditResultScope.FromScope(AuditCanonicalJson.Deserialize<AuditScope>(run.Scope))
                    )
                ),
            "replay_scope_conflict",
            409
        );
        _db.Add(
            new AuditServerSnapshot
            {
                Id = Guid.NewGuid(),
                RunId = run.Id,
                Bytes = prepared.Bytes,
                Sha256 = prepared.Sha256,
                InputContentDigest = prepared.InputContentDigest,
                FrozenAt = snapshot.FrozenAt,
                ExpiresAt = snapshot.ExpiresAt,
            }
        );
        run.EvidenceExpiresAt = MinExpiry(run.EvidenceExpiresAt, snapshot.ExpiresAt);
    }

    private static AuditInput CompatibleReplayInput(byte[] bytes)
    {
        AuditInput input;
        try
        {
            input = AuditCanonicalJson.Deserialize<AuditInput>(bytes);
        }
        catch (AuditValidationException)
        {
            throw new AuditStoreException("unsupported_replay_schema", 409);
        }
        Require(
            input.RuleVersion == AuditVerifierIdentity.RuleVersion
                && Version.TryParse(input.VerifierVersion, out var version)
                && version <= Version.Parse(AuditVerifierIdentity.VerifierVersion),
            "unsupported_replay_version",
            409
        );
        return input;
    }

    public Task RefreshCollectingRunsAsync(CancellationToken ct) =>
        Transaction(
            async () =>
            {
                var now = _clock.GetCurrentInstant();
                var runs = await _db.Set<AuditRun>()
                    .Where(r => r.InstanceId == _instanceId && r.State == "collecting")
                    .OrderBy(r => r.CreatedAt)
                    .Take(_options.MaxQueuedRuns)
                    .ToArrayAsync(ct);
                foreach (var run in runs)
                {
                    await RefreshCollecting(run, false, now, ct);
                }
                await _db.SaveChangesAsync(ct);
                return true;
            },
            ct
        );

    private async Task RefreshCollecting(AuditRun run, bool proceed, Instant now, CancellationToken ct)
    {
        run.Stage = "collectingEvidence";
        var requests = await _db.Set<AuditCaptureRequest>()
            .Where(r => run.CaptureRequestIds.Contains(r.Id))
            .ToArrayAsync(ct);
        var captures = await _db.Set<AuditEvidenceCapture>()
            .Where(c =>
                run.CaptureRequestIds.Contains(c.RequestId)
                && c.DeletedAt == null
                && (c.ExpiresAt == null || c.ExpiresAt > now)
            )
            .ToArrayAsync(ct);
        var ready = requests.Length > 0 && captures.Length == requests.Length;
        if (!ready && !proceed)
        {
            run.DiagnosticCode = "capture_waiting";
            if (requests.Any(r => r.Deadline <= now))
            {
                await Complete(run, "failed", "capture_deadline", now, ct);
            }
            return;
        }
        var scope = AuditCanonicalJson.Deserialize<AuditScope>(run.Scope);
        var existing = await _db.Set<AuditRunCapture>()
            .Where(r => r.RunId == run.Id)
            .Select(r => r.CaptureId)
            .ToArrayAsync(ct);
        var prior = await _db.Set<AuditEvidenceCapture>().Where(c => existing.Contains(c.Id)).ToArrayAsync(ct);
        try
        {
            ValidateCaptureSelection(prior.Concat(captures).DistinctBy(c => c.Id).ToArray(), scope);
        }
        catch (AuditStoreException ex)
        {
            await Complete(run, "failed", ex.Code, now, ct);
            return;
        }
        foreach (var capture in captures.Where(c => !existing.Contains(c.Id)))
        {
            _db.Add(new AuditRunCapture { RunId = run.Id, CaptureId = capture.Id });
            run.EvidenceExpiresAt = MinExpiry(run.EvidenceExpiresAt, capture.ExpiresAt);
        }
        run.PartialEvidenceAcknowledged = proceed && !ready;
        run.State = "queued";
        run.DiagnosticCode = ready ? null : "partial_capture_acknowledged";
        run.Revision++;
    }

    public Task ProceedRunAsync(Guid id, CancellationToken ct) =>
        Transaction(
            async () =>
            {
                var run = await _db.Set<AuditRun>()
                    .SingleOrDefaultAsync(r => r.Id == id && r.InstanceId == _instanceId, ct);
                Require(run != null, "run_not_found", 404);
                Require(run.State == "collecting", "run_not_collecting", 409);
                Require(AuditCanonicalJson.Deserialize<AuditScope>(run.Scope).AllowPartial, "partial_not_allowed", 409);
                await RefreshCollecting(run, true, _clock.GetCurrentInstant(), ct);
                await _db.SaveChangesAsync(ct);
                return true;
            },
            ct
        );

    public Task<bool> SetRunStageAsync(RunLease lease, string stage, CancellationToken ct)
    {
        Require(stage is "collectingEvidence" or "snapshotting" or "verifying", "invalid_run_stage", 400);
        return Transaction(
            async () =>
            {
                var run = await FindLiveRun(lease, _clock.GetCurrentInstant(), ct);
                if (run == null || !await EvidenceAvailable(run.Id, _clock.GetCurrentInstant(), ct))
                {
                    return false;
                }
                run.Stage = stage;
                await _db.SaveChangesAsync(ct);
                return true;
            },
            ct
        );
    }

    public Task CancelRunAsync(Guid id, CancellationToken ct) =>
        Transaction(
            async () =>
            {
                var run = await _db.Set<AuditRun>()
                    .SingleOrDefaultAsync(r => r.Id == id && r.InstanceId == _instanceId, ct);
                Require(run != null, "run_not_found", 404);
                if (!IsTerminal(run.State))
                {
                    await Complete(run, "cancelled", "cancelled", _clock.GetCurrentInstant(), ct);
                    await _db.SaveChangesAsync(ct);
                }
                return true;
            },
            ct
        );

    public async Task<IReadOnlyList<AuditRunView>> ListRunsAsync(
        Instant? before,
        Guid? beforeId,
        int limit,
        CancellationToken ct
    )
    {
        Require(limit is > 0 and <= 200, "invalid_page_limit", 400);
        var query = _db.Set<AuditRun>().AsNoTracking().Where(r => r.InstanceId == _instanceId);
        if (before is { } cutoff && beforeId is { } cursorId)
        {
            query = query.Where(r => r.CreatedAt < cutoff || r.CreatedAt == cutoff && r.Id.CompareTo(cursorId) < 0);
        }
        var ids = await query
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .Take(limit)
            .Select(r => r.Id)
            .ToArrayAsync(ct);
        var results = new List<AuditRunView>();
        foreach (var id in ids)
        {
            results.Add((await GetRunAsync(id, ct))!);
        }
        return results;
    }

    public async Task<AuditCaptureDiscovery> AvailableCapturesAsync(
        SourceSelection selection,
        DateOnly from,
        DateOnly to,
        CancellationToken ct
    )
    {
        const int choiceLimit = 200;
        var now = _clock.GetCurrentInstant();
        var anyMachine = selection.MachineId == Guid.Empty;
        var fromText = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var toText = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        // Seal validates manifest.Scope == request.Scope. Search that bounded metadata in
        // PostgreSQL before limiting or transferring manifests; evidence chunks are never read.
        var captures = await _db.Set<AuditEvidenceCapture>()
            .FromSqlInterpolated(
                $"""
                SELECT c.* FROM "AuditEvidenceCaptures" AS c
                INNER JOIN "AuditCaptureRequests" AS r ON r."Id"=c."RequestId"
                WHERE c."DeletedAt" IS NULL AND (c."ExpiresAt" IS NULL OR c."ExpiresAt">{now})
                    AND ({anyMachine} OR c."MachineId"={selection.MachineId})
                    AND (convert_from(r."Scope",'UTF8')::jsonb->>'from')<={fromText}
                    AND (convert_from(r."Scope",'UTF8')::jsonb->>'to')>={toText}
                    AND EXISTS (
                        SELECT 1 FROM jsonb_array_elements(convert_from(r."Scope",'UTF8')::jsonb->'sources') AS scoped_source
                        WHERE scoped_source->>'sourceId'={selection.SourceId}
                    )
                """
            )
            .AsNoTracking()
            .OrderByDescending(c => c.SealedAt)
            .ThenByDescending(c => c.Id)
            .Take(choiceLimit + 1)
            .Select(c => new AvailableAuditCapture(c.Id, c.MachineId, c.Manifest, c.ExpiresAt))
            .ToArrayAsync(ct);
        return new(captures.Take(choiceLimit).ToArray(), captures.Length > choiceLimit);
    }

    public async Task<(AuditScope Scope, Guid[] CaptureIds)> ReplaySelectionAsync(Guid id, CancellationToken ct)
    {
        var run = await GetRunAsync(id, ct);
        Require(run != null, "run_not_found", 404);
        var ids = await _db.Set<AuditRunCapture>().Where(r => r.RunId == id).Select(r => r.CaptureId).ToArrayAsync(ct);
        return (AuditCanonicalJson.Deserialize<AuditScope>(run.Scope) with { EvaluationOfRunId = id }, ids);
    }
}
