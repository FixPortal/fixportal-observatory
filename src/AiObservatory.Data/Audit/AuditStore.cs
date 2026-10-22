using System.Text;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace AiObservatory.Data.Audit;

/// <summary>
/// Endpoint-oriented audit transactions. Every mutation takes EvidenceLockKey before reading audit
/// state and holds it only through bounded database work. Never hold it during verification or streaming.
/// Entities remain internal; consumers receive detached transport values or retained original bytes.
/// </summary>
public sealed partial class AuditStore
{
    public const long EvidenceLockKey = 0x4155444954455644;
    private readonly AiObservatoryDbContext _db;
    private readonly IClock _clock;
    private readonly AuditStoreOptions _options;
    private readonly string _instanceId;

    public AuditStore(
        AiObservatoryDbContext db,
        IClock clock,
        AuditStoreOptions? options = null,
        string instanceId = "default"
    )
    {
        _db = db;
        _clock = clock;
        _options = options ?? new();
        Require(_options.ValidationErrors(clock.GetCurrentInstant()).Count == 0, "invalid_audit_options", 400);
        ValidateKey(instanceId);
        _instanceId = instanceId;
    }

    public Task<MachineRegistration> RegisterMachineAsync(byte[] credentialHash, CancellationToken ct)
    {
        Require(credentialHash.Length == 32, "invalid_credential_hash", 400);
        var hash = credentialHash.ToArray();
        return Transaction(
            async () =>
            {
                var machine = new AuditMachine { Id = Guid.NewGuid(), CreatedAt = _clock.GetCurrentInstant() };
                var credential = new AuditMachineCredential
                {
                    Id = Guid.NewGuid(),
                    MachineId = machine.Id,
                    SecretHash = hash,
                    CreatedAt = machine.CreatedAt,
                };
                _db.Add(machine);
                _db.Add(credential);
                await _db.SaveChangesAsync(ct);
                return new MachineRegistration(machine.Id, credential.Id);
            },
            ct
        );
    }

    public Task RevokeCredentialAsync(Guid credentialId, CancellationToken ct) =>
        Transaction(
            async () =>
            {
                var credential = await _db.Set<AuditMachineCredential>()
                    .SingleOrDefaultAsync(x => x.Id == credentialId, ct);
                if (credential != null && credential.RevokedAt == null)
                {
                    credential.RevokedAt = _clock.GetCurrentInstant();
                    await _db.SaveChangesAsync(ct);
                }
                return true;
            },
            ct
        );

    public async Task<AuditCredentialView?> FindCredentialAsync(Guid credentialId, CancellationToken ct) =>
        await _db.Set<AuditMachineCredential>()
            .AsNoTracking()
            .Where(x => x.Id == credentialId)
            .Select(x => new AuditCredentialView(x.MachineId, x.Id, x.SecretHash, x.RevokedAt != null))
            .SingleOrDefaultAsync(ct);

    public Task<MachineRegistration> RotateMachineCredentialAsync(Guid machineId, byte[] hash, CancellationToken ct)
    {
        Require(hash.Length == 32, "invalid_credential_hash", 400);
        var original = hash.ToArray();
        return Transaction(
            async () =>
            {
                Require(await _db.Set<AuditMachine>().AnyAsync(x => x.Id == machineId, ct), "machine_not_found", 404);
                await RevokeMachineCredentials(machineId, ct);
                var credential = new AuditMachineCredential
                {
                    Id = Guid.NewGuid(),
                    MachineId = machineId,
                    SecretHash = original,
                    CreatedAt = _clock.GetCurrentInstant(),
                };
                _db.Add(credential);
                await _db.SaveChangesAsync(ct);
                return new MachineRegistration(machineId, credential.Id);
            },
            ct
        );
    }

    public Task RevokeMachineCredentialAsync(Guid machineId, CancellationToken ct) =>
        Transaction(
            async () =>
            {
                await RevokeMachineCredentials(machineId, ct);
                return true;
            },
            ct
        );

    private Task<int> RevokeMachineCredentials(Guid machineId, CancellationToken ct) =>
        _db.Set<AuditMachineCredential>()
            .Where(x => x.MachineId == machineId && x.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, _clock.GetCurrentInstant()), ct);

    public Task<CaptureLease> ResolveCaptureLeaseAsync(
        Guid requestId,
        Guid machineId,
        Guid credentialId,
        Guid token,
        CancellationToken ct
    ) =>
        Transaction(
            async () =>
            {
                var candidate = new CaptureLease(requestId, machineId, credentialId, token, default, null!);
                var request = await RequireCaptureLease(candidate, allowSealed: true, ct);
                return candidate with
                {
                    ExpiresAt = request.LeaseExpiresAt!.Value,
                    Scope = AuditCanonicalJson.Deserialize<AuditScope>(request.Scope),
                };
            },
            ct
        );

    public Task<Guid> CreateCaptureRequestAsync(Guid machineId, AuditScope scope, Instant now, CancellationToken ct)
    {
        var bytes = ValidateScope(scope);
        Require(scope.Sources.All(x => x.MachineId == machineId), "capture_scope_mismatch", 400);
        return Transaction(
            async () =>
            {
                Require(await _db.Set<AuditMachine>().AnyAsync(x => x.Id == machineId, ct), "machine_not_found", 404);
                var pending = await _db.Set<AuditCaptureRequest>()
                    .CountAsync(
                        x =>
                            x.MachineId == machineId
                            && (x.State == "pending" || x.State == "uploading")
                            && x.Deadline > now,
                        ct
                    );
                Require(pending < _options.MaxOutstandingRequestsPerMachine, "capture_queue_full", 429);
                var request = new AuditCaptureRequest
                {
                    Id = Guid.NewGuid(),
                    MachineId = machineId,
                    Scope = bytes,
                    CreatedAt = now,
                    Deadline = now.Plus(Duration.FromTimeSpan(_options.CaptureRequestDeadline)),
                };
                _db.Add(request);
                await _db.SaveChangesAsync(ct);
                return request.Id;
            },
            ct
        );
    }

    public Task<CaptureLease?> TryClaimCaptureAsync(
        Guid machineId,
        Guid credentialId,
        Instant now,
        CancellationToken ct
    ) =>
        Transaction<CaptureLease?>(
            async () =>
            {
                await RequireCredential(machineId, credentialId, ct);
                var request = await _db.Set<AuditCaptureRequest>()
                    .Where(x =>
                        x.MachineId == machineId
                        && x.Deadline > now
                        && (
                            x.State == "pending"
                            || x.State == "uploading" && (x.LeaseExpiresAt <= now || x.CredentialId == credentialId)
                        )
                    )
                    .OrderBy(x => x.CreatedAt)
                    .ThenBy(x => x.Id)
                    .FirstOrDefaultAsync(ct);
                if (request == null)
                {
                    return null;
                }
                if (request.LeaseExpiresAt <= now || request.CredentialId != credentialId || request.LeaseToken == null)
                {
                    // A new scan must not inherit retained partial bytes from an expired owner.
                    // Live-token retry leaves both bytes and counters unchanged; sealed requests are never selected.
                    await _db.Set<AuditEvidenceChunk>().Where(x => x.RequestId == request.Id).ExecuteDeleteAsync(ct);
                    request.UploadedBytes = 0;
                    request.RecordCount = 0;
                    request.LeaseToken = Guid.NewGuid();
                }
                request.CredentialId = credentialId;
                request.LeaseExpiresAt = Min(
                    request.Deadline,
                    now.Plus(Duration.FromTimeSpan(_options.RunLeaseDuration))
                );
                request.State = "uploading";
                request.Revision++;
                await _db.SaveChangesAsync(ct);
                return new CaptureLease(
                    request.Id,
                    machineId,
                    credentialId,
                    request.LeaseToken.Value,
                    request.LeaseExpiresAt.Value,
                    AuditCanonicalJson.Deserialize<AuditScope>(request.Scope)
                );
            },
            ct
        );

    public Task PutChunkAsync(CaptureLease lease, int index, byte[] bytes, string sha256, CancellationToken ct)
    {
        Require(index is >= 0 and < 10000, "invalid_chunk_index", 400);
        Require(bytes.Length > 0 && bytes.Length <= _options.MaxChunkBytes, "chunk_size_limit", 413);
        var original = bytes.ToArray();
        Require(AuditCanonicalJson.ContentHash(original) == sha256, "chunk_hash_mismatch", 400);
        var records = ParseRecords(original);
        return Transaction(
            async () =>
            {
                var request = await RequireCaptureLease(lease, allowSealed: false, ct);
                var existing = await _db.Set<AuditEvidenceChunk>()
                    .SingleOrDefaultAsync(x => x.RequestId == lease.RequestId && x.Index == index, ct);
                if (existing != null)
                {
                    Require(
                        existing.Sha256 == sha256 && existing.Bytes.AsSpan().SequenceEqual(original),
                        "chunk_retry_conflict",
                        409
                    );
                    return true;
                }
                Require(
                    request.UploadedBytes + original.Length <= _options.MaxCaptureBytes
                        && request.RecordCount + records.Count <= _options.MaxRecordsPerCapture,
                    "capture_size_limit",
                    413
                );
                request.UploadedBytes += original.Length;
                request.RecordCount += records.Count;
                request.Revision++;
                _db.Add(
                    new AuditEvidenceChunk
                    {
                        RequestId = lease.RequestId,
                        Index = index,
                        Bytes = original,
                        Sha256 = sha256,
                        RecordCount = records.Count,
                    }
                );
                await _db.SaveChangesAsync(ct);
                return true;
            },
            ct
        );
    }

    public Task<Guid> SealCaptureAsync(CaptureLease lease, byte[] manifest, string idempotencyKey, CancellationToken ct)
    {
        ValidateKey(idempotencyKey);
        Require(manifest.Length <= 1024 * 1024, "manifest_size_limit", 413);
        var original = manifest.ToArray();
        var parsed = AuditCanonicalJson.Deserialize<CaptureManifest>(original);
        Require(original.AsSpan().SequenceEqual(AuditCanonicalJson.Serialize(parsed)), "noncanonical_manifest", 400);
        return Transaction(
            async () =>
            {
                var request = await RequireCaptureLease(lease, allowSealed: true, ct);
                var existing = await _db.Set<AuditEvidenceCapture>()
                    .SingleOrDefaultAsync(x => x.RequestId == lease.RequestId, ct);
                if (existing != null)
                {
                    Require(
                        existing.DeletedAt == null
                            && existing.SealKey == idempotencyKey
                            && existing.Manifest.AsSpan().SequenceEqual(original),
                        "seal_retry_conflict",
                        409
                    );
                    return existing.Id;
                }
                Require(
                    parsed.RequestId == request.Id
                        && parsed.MachineId == request.MachineId
                        && AuditCanonicalJson.Serialize(parsed.Scope).AsSpan().SequenceEqual(request.Scope),
                    "manifest_scope_mismatch",
                    409
                );
                var chunks = await _db.Set<AuditEvidenceChunk>()
                    .Where(x => x.RequestId == request.Id)
                    .OrderBy(x => x.Index)
                    .ToListAsync(ct);
                Require(request.UploadedBytes + original.Length <= _options.MaxCaptureBytes, "capture_size_limit", 413);
                Require(
                    chunks.Count == parsed.ChunkHashes.Count && request.RecordCount == parsed.RecordCount,
                    "capture_count_mismatch",
                    409
                );
                var recordHashes =
                    new Dictionary<(string SourceId, string Generation, string FileId, long Sequence), string>();
                for (var index = 0; index < chunks.Count; index++)
                {
                    var chunk = chunks[index];
                    Require(
                        chunk.Index == index
                            && chunk.Sha256 == parsed.ChunkHashes[index]
                            && AuditCanonicalJson.ContentHash(chunk.Bytes) == chunk.Sha256,
                        "capture_chunk_mismatch",
                        409
                    );
                    foreach (var record in ParseRecords(chunk.Bytes))
                    {
                        Require(
                            record.MachineId == parsed.MachineId
                                && record.Generation == parsed.Generation
                                && parsed.Scope.Sources.Any(x =>
                                    x.MachineId == record.MachineId && x.SourceId == record.SourceId
                                )
                                && parsed.SourceFormats.Any(x =>
                                    x.SourceId == record.SourceId
                                    && x.Format == record.SourceFormat
                                    && x.Version == record.SourceVersion
                                ),
                            "capture_record_membership",
                            409
                        );
                        ValidateRecordIdentity(record, recordHashes);
                    }
                }
                var now = _clock.GetCurrentInstant();
                var retention = await Retention(ct);
                _db.Add(
                    new AuditEvidenceCapture
                    {
                        Id = parsed.CaptureId,
                        RequestId = request.Id,
                        MachineId = request.MachineId,
                        Generation = parsed.Generation,
                        Manifest = original,
                        ManifestHash = AuditCanonicalJson.ContentHash(original),
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

    public Task<RetainedCapture?> ReadCaptureAsync(Guid captureId, CancellationToken ct) =>
        Transaction<RetainedCapture?>(
            async () =>
            {
                var capture = await _db.Set<AuditEvidenceCapture>()
                    .AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == captureId, ct);
                if (capture == null || capture.DeletedAt != null || capture.ExpiresAt <= _clock.GetCurrentInstant())
                {
                    return null;
                }
                var chunks = await _db.Set<AuditEvidenceChunk>()
                    .AsNoTracking()
                    .Where(x => x.RequestId == capture.RequestId)
                    .OrderBy(x => x.Index)
                    .Select(x => x.Bytes)
                    .ToListAsync(ct);
                return new RetainedCapture(capture.Manifest, chunks, capture.ExpiresAt);
            },
            ct
        );

    public async Task<Guid> CreateRunAsync(
        AuditScope scope,
        IReadOnlyList<Guid> captureIds,
        string idempotencyKey,
        bool collecting,
        Instant now,
        CancellationToken ct,
        FrozenPresentationProbe? presentationProbe = null
    )
    {
        var scopeBytes = ValidateScope(scope);
        ValidateKey(idempotencyKey);
        var ids = captureIds.Distinct().Order().ToArray();
        Require(
            ids.Length <= 200 && ids.Length == captureIds.Count && ids.All(x => x != Guid.Empty),
            "invalid_capture_selection",
            400
        );
        var hash = AuditCanonicalJson.ContentHash([
            .. scopeBytes,
            .. Encoding.UTF8.GetBytes(string.Join(',', ids)),
            (byte)(collecting ? 1 : 0),
        ]);
        hash = ProbeRequestHash(scope, presentationProbe, hash);
        var replay = await PrepareReplayAsync(scope, presentationProbe, ct);
        return await Transaction(
            async () =>
            {
                var existing = await _db.Set<AuditRun>()
                    .SingleOrDefaultAsync(x => x.InstanceId == _instanceId && x.IdempotencyKey == idempotencyKey, ct);
                if (existing != null)
                {
                    Require(existing.RequestHash == hash, "run_retry_conflict", 409);
                    return existing.Id;
                }
                var queued = await _db.Set<AuditRun>()
                    .CountAsync(
                        x => x.InstanceId == _instanceId && (x.State == "queued" || x.State == "collecting"),
                        ct
                    );
                Require(queued < _options.MaxQueuedRuns, "run_queue_full", 429);
                var captures = await _db.Set<AuditEvidenceCapture>().Where(x => ids.Contains(x.Id)).ToListAsync(ct);
                Require(
                    captures.Count == ids.Length
                        && captures.All(x => x.DeletedAt == null && (x.ExpiresAt == null || x.ExpiresAt > now)),
                    "capture_unavailable",
                    410
                );
                ValidateCaptureSelection(captures, scope);
                var run = new AuditRun
                {
                    Id = Guid.NewGuid(),
                    InstanceId = _instanceId,
                    Scope = scopeBytes,
                    IdempotencyKey = idempotencyKey,
                    RequestHash = hash,
                    State = collecting ? "collecting" : "queued",
                    CaptureRequestIds = await CreateRunCaptureRequests(scope, collecting, now, ct),
                    CreatedAt = now,
                    PredecessorRunId = scope.PredecessorRunId,
                    EvaluationOfRunId = scope.EvaluationOfRunId,
                    EvidenceExpiresAt = captures.Select(x => x.ExpiresAt).Where(x => x != null).Min(),
                };
                await PinReplay(run, ids, replay, _clock.GetCurrentInstant(), ct);
                _db.Add(run);
                foreach (var id in ids)
                {
                    _db.Add(new AuditRunCapture { RunId = run.Id, CaptureId = id });
                }
                await _db.SaveChangesAsync(ct);
                return run.Id;
            },
            ct
        );
    }

    public Task<RunLease?> TryLeaseRunAsync(Guid workerId, Instant now, CancellationToken ct)
    {
        Require(workerId != Guid.Empty, "invalid_worker", 400);
        return Transaction<RunLease?>(
            async () =>
            {
                var expired = await _db.Set<AuditRun>()
                    .Where(x =>
                        x.InstanceId == _instanceId
                        && x.State == "executing"
                        && x.LeaseExpiresAt <= now
                        && x.Attempts >= _options.MaxExecutionAttempts
                    )
                    .ToListAsync(ct);
                foreach (var run in expired)
                {
                    await Complete(run, "failed", "lease_expired", now, ct);
                }
                await _db.SaveChangesAsync(ct);
                if (
                    await _db.Set<AuditRun>()
                        .CountAsync(
                            x => x.InstanceId == _instanceId && x.State == "executing" && x.LeaseExpiresAt > now,
                            ct
                        ) >= _options.MaxExecutingRuns
                )
                {
                    return null;
                }
                var next = await _db.Set<AuditRun>()
                    .Where(x =>
                        x.InstanceId == _instanceId
                        && (x.State == "queued" || x.State == "executing" && x.LeaseExpiresAt <= now)
                        && x.Attempts < _options.MaxExecutionAttempts
                    )
                    .OrderBy(x => x.CreatedAt)
                    .ThenBy(x => x.Id)
                    .FirstOrDefaultAsync(ct);
                if (next == null)
                {
                    return null;
                }
                if (!await EvidenceAvailable(next.Id, now, ct))
                {
                    await Complete(next, "failed", "evidence_unavailable", now, ct);
                    await _db.SaveChangesAsync(ct);
                    return null;
                }
                next.State = "executing";
                next.WorkerId = workerId;
                next.LeaseExpiresAt = now.Plus(Duration.FromTimeSpan(_options.RunLeaseDuration));
                next.Attempts++;
                next.Revision++;
                await _db.SaveChangesAsync(ct);
                return new RunLease(next.Id, workerId, next.Revision, next.LeaseExpiresAt.Value);
            },
            ct
        );
    }

    public Task<bool> RenewRunLeaseAsync(RunLease lease, Instant now, CancellationToken ct) =>
        Transaction(
            async () =>
            {
                var run = await FindLiveRun(lease, now, ct);
                if (run == null || !await EvidenceAvailable(run.Id, now, ct))
                {
                    return false;
                }
                run.LeaseExpiresAt = now.Plus(Duration.FromTimeSpan(_options.RunLeaseDuration));
                await _db.SaveChangesAsync(ct);
                return true;
            },
            ct
        );

    public Task<bool> TryPublishAsync(RunLease lease, AuditResult result, CancellationToken ct)
    {
        var bytes = AuditCanonicalJson.Serialize(result);
        var summary = SanitizedResult(result);
        return Transaction(
            async () =>
            {
                var now = _clock.GetCurrentInstant();
                var run = await FindLiveRun(lease, now, ct);
                if (run == null || !await EvidenceAvailable(run.Id, now, ct))
                {
                    return false;
                }
                Require(
                    AuditCanonicalJson
                        .Serialize(result.Scope)
                        .AsSpan()
                        .SequenceEqual(
                            AuditCanonicalJson.Serialize(
                                AuditResultScope.FromScope(AuditCanonicalJson.Deserialize<AuditScope>(run.Scope))
                            )
                        ),
                    "result_scope_mismatch",
                    409
                );
                var snapshot = await _db.Set<AuditServerSnapshot>().SingleOrDefaultAsync(x => x.RunId == run.Id, ct);
                if (snapshot == null)
                {
                    return false;
                }
                Require(result.InputContentDigest == snapshot.InputContentDigest, "result_input_mismatch", 409);
                run.Result = bytes;
                run.SanitizedSummary = summary;
                run.ServerCutoff = snapshot.FrozenAt;
                run.ResultHash = AuditCanonicalJson.ContentHash(bytes);
                await Complete(run, "completed", null, now, ct);
                for (var i = 0; i < result.Findings.Count; i++)
                {
                    _db.Add(
                        new AuditFindingRecord
                        {
                            RunId = run.Id,
                            Index = i,
                            Finding = AuditCanonicalJson.Serialize(result.Findings[i]),
                        }
                    );
                }
                await _db.SaveChangesAsync(ct);
                return true;
            },
            ct
        );
    }

    public Task<bool> FailRunAsync(RunLease lease, string diagnosticCode, CancellationToken ct)
    {
        ValidateKey(diagnosticCode);
        Require(diagnosticCode.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'), "invalid_diagnostic", 400);
        return Transaction(
            async () =>
            {
                var now = _clock.GetCurrentInstant();
                var run = await FindLiveRun(lease, now, ct);
                if (run == null)
                {
                    return false;
                }
                if (run.Attempts >= _options.MaxExecutionAttempts)
                {
                    await Complete(run, "failed", diagnosticCode, now, ct);
                }
                else
                {
                    run.Revision++;
                    run.WorkerId = null;
                    run.LeaseExpiresAt = now;
                    run.DiagnosticCode = diagnosticCode;
                }
                await _db.SaveChangesAsync(ct);
                return true;
            },
            ct
        );
    }

    public Task<bool> CancelRunAsync(Guid runId, long revision, CancellationToken ct) =>
        Transaction(
            async () =>
            {
                var run = await _db.Set<AuditRun>()
                    .SingleOrDefaultAsync(x => x.Id == runId && x.InstanceId == _instanceId, ct);
                if (run == null)
                {
                    return false;
                }
                if (run.State == "cancelled")
                {
                    return true;
                }
                if (run.Revision != revision || IsTerminal(run.State))
                {
                    return false;
                }
                await Complete(run, "cancelled", "cancelled", _clock.GetCurrentInstant(), ct);
                await _db.SaveChangesAsync(ct);
                return true;
            },
            ct
        );

    public Task<AuditRunView?> GetRunAsync(Guid runId, CancellationToken ct) =>
        _db.Set<AuditRun>()
            .AsNoTracking()
            .Where(x =>
                x.Id == runId
                && x.InstanceId == _instanceId
                && (x.SummaryExpiresAt == null || x.SummaryExpiresAt > _clock.GetCurrentInstant())
            )
            .Select(x => new AuditRunView(
                x.Id,
                x.State,
                x.Revision,
                x.Attempts,
                x.DiagnosticCode,
                x.EvidenceExpiresAt,
                x.SummaryExpiresAt,
                x.EvidenceExpiresAt != null && x.EvidenceExpiresAt <= _clock.GetCurrentInstant() ? null : x.Result
            )
            {
                SanitizedSummary = x.SanitizedSummary,
                OriginalResultHash = x.ResultHash,
                Stage = x.Stage,
                CreatedAt = x.CreatedAt,
                ServerCutoff =
                    x.ServerCutoff
                    ?? _db.Set<AuditServerSnapshot>()
                        .Where(s => s.RunId == x.Id)
                        .Select(s => (Instant?)s.FrozenAt)
                        .FirstOrDefault(),
                Scope = x.Scope,
                PredecessorRunId = x.PredecessorRunId,
                EvaluationOfRunId = x.EvaluationOfRunId,
            })
            .SingleOrDefaultAsync(ct);

    /// <summary>Copies immutable capture bytes under the evidence lock and the caller's ownership fence.</summary>
    public Task<SnapshotMaterialization> ReadSnapshotMaterializationAsync(RunLease lease, CancellationToken ct) =>
        Transaction(
            async () =>
            {
                var now = _clock.GetCurrentInstant();
                var run = await FindLiveRun(lease, now, ct);
                Require(run is not null && await EvidenceAvailable(lease.RunId, now, ct), "snapshot_lease_lost", 409);
                var captureIds = await _db.Set<AuditRunCapture>()
                    .AsNoTracking()
                    .Where(x => x.RunId == lease.RunId)
                    .Select(x => x.CaptureId)
                    .ToArrayAsync(ct);
                var captures = new List<RetainedCapture>();
                long totalBytes = 0;
                foreach (var id in captureIds)
                {
                    var capture = await _db.Set<AuditEvidenceCapture>().AsNoTracking().SingleAsync(x => x.Id == id, ct);
                    var chunkBytes = await _db.Set<AuditEvidenceChunk>()
                        .Where(x => x.RequestId == capture.RequestId)
                        .SumAsync(x => (long)x.Bytes.Length, ct);
                    totalBytes = checked(totalBytes + capture.Manifest.LongLength + chunkBytes);
                    Require(totalBytes <= _options.MaxSnapshotBytes, "snapshot_size_limit", 413);
                    var chunks = await _db.Set<AuditEvidenceChunk>()
                        .AsNoTracking()
                        .Where(x => x.RequestId == capture.RequestId)
                        .OrderBy(x => x.Index)
                        .Select(x => x.Bytes)
                        .ToArrayAsync(ct);
                    captures.Add(new(capture.Manifest, chunks, capture.ExpiresAt));
                }
                return new SnapshotMaterialization(AuditCanonicalJson.Deserialize<AuditScope>(run.Scope), captures)
                {
                    PartialEvidenceAcknowledged = run.PartialEvidenceAcknowledged,
                };
            },
            ct
        );

    public Task<bool> TryStoreSnapshotAsync(
        RunLease lease,
        byte[] bytes,
        string inputContentDigest,
        CancellationToken ct
    )
    {
        Require(bytes.Length > 0 && bytes.Length <= _options.MaxSnapshotBytes, "snapshot_size_limit", 413);
        Require(
            inputContentDigest.Length == 64
                && inputContentDigest.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f'),
            "invalid_input_digest",
            400
        );
        var original = bytes.ToArray();
        return Transaction(
            async () =>
            {
                var now = _clock.GetCurrentInstant();
                var run = await FindLiveRun(lease, now, ct);
                if (run == null || !await EvidenceAvailable(run.Id, now, ct))
                {
                    return false;
                }
                var existing = await _db.Set<AuditServerSnapshot>().SingleOrDefaultAsync(x => x.RunId == run.Id, ct);
                if (existing != null)
                {
                    Require(
                        existing.InputContentDigest == inputContentDigest
                            && existing.Bytes.AsSpan().SequenceEqual(original),
                        "snapshot_retry_conflict",
                        409
                    );
                    return true;
                }
                var retention = await Retention(ct);
                var expiry = Expiry(now, retention.EvidenceDays);
                _db.Add(
                    new AuditServerSnapshot
                    {
                        Id = Guid.NewGuid(),
                        RunId = run.Id,
                        Revision = lease.Revision,
                        Bytes = original,
                        Sha256 = AuditCanonicalJson.ContentHash(original),
                        InputContentDigest = inputContentDigest,
                        FrozenAt = now,
                        ExpiresAt = expiry,
                    }
                );
                run.EvidenceExpiresAt = MinExpiry(run.EvidenceExpiresAt, expiry);
                await _db.SaveChangesAsync(ct);
                return true;
            },
            ct
        );
    }

    public Task<RetainedSnapshot?> ReadSnapshotAsync(Guid runId, CancellationToken ct)
    {
        return Transaction<RetainedSnapshot?>(
            async () =>
            {
                var now = _clock.GetCurrentInstant();
                if (
                    !await _db.Set<AuditRun>().AnyAsync(x => x.Id == runId && x.InstanceId == _instanceId, ct)
                    || !await EvidenceAvailable(runId, now, ct)
                )
                {
                    return null;
                }
                return await _db.Set<AuditServerSnapshot>()
                    .AsNoTracking()
                    .Where(x => x.RunId == runId)
                    .Select(x => new RetainedSnapshot(x.Bytes, x.Sha256, x.InputContentDigest, x.ExpiresAt)
                    {
                        SnapshotId = x.Id,
                        RunRevision = _db.Set<AuditRun>().Where(r => r.Id == runId).Select(r => r.Revision).First(),
                    })
                    .SingleOrDefaultAsync(ct);
            },
            ct
        );
    }

    public Task<string> CaptureDeletionImpactAsync(Guid captureId, CancellationToken ct) =>
        Transaction(() => Impact(captureId, ct), ct);

    public async Task DeleteCaptureAsync(Guid captureId, string impactDigest, CancellationToken ct)
    {
        await FenceCaptureDeletionAsync(captureId, impactDigest, ct);
        while (!await PurgeUnavailableCaptureAsync(captureId, ct))
        {
            // Export cancellation and release happen outside EvidenceLockKey.
            await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
        }
    }

    public Task UpdateRetentionAsync(int? evidenceDays, int? summaryDays, CancellationToken ct)
    {
        return Transaction(
            async () =>
            {
                var now = _clock.GetCurrentInstant();
                Require(
                    AuditRetentionPolicy.TryGetExpiry(now, evidenceDays, out _)
                        && AuditRetentionPolicy.TryGetExpiry(now, summaryDays, out _),
                    "invalid_retention",
                    400
                );
                var settings = await Retention(ct);
                settings.EvidenceDays = evidenceDays;
                settings.SummaryDays = summaryDays;
                settings.Revision++;
                await _db.SaveChangesAsync(ct);
                return true;
            },
            ct
        );
    }

    private async Task<T> Transaction<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        // Discard only this bounded model's tracked entities so a reused scoped context never
        // acts on a pre-lock view. Ordinary usage/spend tracked entities are not affected.
        foreach (
            var entry in _db
                .ChangeTracker.Entries()
                .Where(x => x.Entity.GetType().Namespace == typeof(AuditStore).Namespace)
                .ToList()
        )
        {
            entry.State = EntityState.Detached;
        }
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({EvidenceLockKey})", ct);
        var result = await operation();
        await transaction.CommitAsync(ct);
        return result;
    }

    private async Task RequireCredential(Guid machineId, Guid credentialId, CancellationToken ct)
    {
        Require(
            await _db.Set<AuditMachineCredential>()
                .AnyAsync(x => x.Id == credentialId && x.MachineId == machineId && x.RevokedAt == null, ct),
            "credential_revoked",
            403
        );
    }

    private async Task<AuditCaptureRequest> RequireCaptureLease(
        CaptureLease lease,
        bool allowSealed,
        CancellationToken ct
    )
    {
        await RequireCredential(lease.MachineId, lease.CredentialId, ct);
        var now = _clock.GetCurrentInstant();
        var request = await _db.Set<AuditCaptureRequest>()
            .SingleOrDefaultAsync(x => x.Id == lease.RequestId && x.MachineId == lease.MachineId, ct);
        Require(request != null, "capture_not_found", 404);
        Require(
            request.CredentialId == lease.CredentialId && request.LeaseToken == lease.Token,
            "capture_lease_conflict",
            409
        );
        Require(request.Deadline > now && request.LeaseExpiresAt > now, "capture_lease_expired", 410);
        Require(request.State == "uploading" || allowSealed && request.State == "sealed", "capture_immutable", 409);
        return request;
    }

    private Task<AuditRun?> FindLiveRun(RunLease lease, Instant now, CancellationToken ct) =>
        _db.Set<AuditRun>()
            .SingleOrDefaultAsync(
                x =>
                    x.Id == lease.RunId
                    && x.InstanceId == _instanceId
                    && x.State == "executing"
                    && x.WorkerId == lease.WorkerId
                    && x.Revision == lease.Revision
                    && x.LeaseExpiresAt > now,
                ct
            );

    private async Task<bool> EvidenceAvailable(Guid runId, Instant now, CancellationToken ct) =>
        !await _db.Set<AuditRunCapture>()
            .AnyAsync(
                link =>
                    link.RunId == runId
                    && _db.Set<AuditEvidenceCapture>()
                        .Any(capture =>
                            capture.Id == link.CaptureId && (capture.DeletedAt != null || capture.ExpiresAt <= now)
                        ),
                ct
            ) && !await _db.Set<AuditServerSnapshot>().AnyAsync(x => x.RunId == runId && x.ExpiresAt <= now, ct);

    private async Task Complete(AuditRun run, string state, string? diagnostic, Instant now, CancellationToken ct)
    {
        var retention = await Retention(ct);
        run.State = state;
        run.Stage = state;
        run.DiagnosticCode = diagnostic;
        run.CompletedAt = now;
        run.SummaryExpiresAt = Expiry(now, retention.SummaryDays);
        run.Revision++;
        run.WorkerId = null;
        run.LeaseExpiresAt = null;
    }

    private async Task<AuditRetentionSettings> Retention(CancellationToken ct)
    {
        var settings =
            _db.Set<AuditRetentionSettings>().Local.SingleOrDefault(x => x.InstanceId == _instanceId)
            ?? await _db.Set<AuditRetentionSettings>().SingleOrDefaultAsync(x => x.InstanceId == _instanceId, ct);
        if (settings != null)
        {
            return settings;
        }
        settings = new()
        {
            InstanceId = _instanceId,
            EvidenceDays = _options.EvidenceRetentionDays,
            SummaryDays = _options.SummaryRetentionDays,
        };
        _db.Add(settings);
        return settings;
    }

    private async Task<string> Impact(Guid captureId, CancellationToken ct)
    {
        var capture = await _db.Set<AuditEvidenceCapture>()
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == captureId, ct);
        Require(capture != null, "capture_not_found", 404);
        var runs = await DependentRuns(captureId, capture.RequestId)
            .AsNoTracking()
            .OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.Revision })
            .ToListAsync(ct);
        var text = $"{capture.Id:D}:{capture.Revision}:" + string.Join(',', runs.Select(x => $"{x.Id:D}:{x.Revision}"));
        return AuditCanonicalJson.ContentHash(Encoding.UTF8.GetBytes(text));
    }

    private static void ValidateCaptureSelection(IReadOnlyList<AuditEvidenceCapture> captures, AuditScope scope)
    {
        var manifests = captures.Select(c => AuditCanonicalJson.Deserialize<CaptureManifest>(c.Manifest)).ToArray();
        foreach (var first in manifests)
        {
            Require(
                !manifests.Any(second =>
                    first.CaptureId != second.CaptureId
                    && first.MachineId == second.MachineId
                    && first.Generation != second.Generation
                    && first.Scope.From <= second.Scope.To
                    && second.Scope.From <= first.Scope.To
                    && first.Scope.Sources.Intersect(second.Scope.Sources).Any()
                ),
                "identity_generation_ambiguous",
                409
            );
        }
        foreach (var capture in captures)
        {
            var manifest = AuditCanonicalJson.Deserialize<CaptureManifest>(capture.Manifest);
            Require(
                manifest.Scope.From <= scope.To
                    && manifest.Scope.To >= scope.From
                    && manifest.Scope.Sources.All(s => scope.Sources.Contains(s)),
                "capture_scope_mismatch",
                409
            );
        }
    }

    private byte[] ValidateScope(AuditScope scope)
    {
        var bytes = AuditCanonicalJson.Serialize(scope);
        Require(scope.Sources.Count <= 32, "source_selection_limit", 413);
        Require(scope.To.DayNumber - scope.From.DayNumber + 1 <= _options.MaxRunWindowDays, "run_window_limit", 400);
        return bytes;
    }

    private static void ValidateRecordIdentity(
        SourceUsageRecord record,
        IDictionary<(string SourceId, string Generation, string FileId, long Sequence), string> recordHashes
    )
    {
        var identity = (record.SourceId, record.Generation, record.FileId, record.Sequence);
        var hash = AuditCanonicalJson.ContentHash(AuditCanonicalJson.Serialize(record));
        Require(
            !recordHashes.TryGetValue(identity, out var existing) || existing == hash,
            "capture_record_identity_conflict",
            409
        );
        recordHashes[identity] = hash;
    }

    private static IReadOnlyList<SourceUsageRecord> ParseRecords(byte[] bytes)
    {
        var records = new List<SourceUsageRecord>();
        var start = 0;
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] != 10)
            {
                continue;
            }
            Require(index > start && index - start <= 1024 * 1024, "invalid_chunk_records", 400);
            var line = bytes.AsSpan(start, index - start);
            var record = AuditCanonicalJson.Deserialize<SourceUsageRecord>(line);
            Require(line.SequenceEqual(AuditCanonicalJson.Serialize(record)), "noncanonical_chunk", 400);
            records.Add(record);
            start = index + 1;
        }
        Require(start == bytes.Length && records.Count > 0, "invalid_chunk_records", 400);
        return records;
    }

    private static void ValidateKey(string key) =>
        Require(
            key.Length is > 0 and <= 128 && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'),
            "invalid_key",
            400
        );

    private static bool IsTerminal(string state) => state is "completed" or "cancelled" or "failed";

    private static Instant? Expiry(Instant from, int? days)
    {
        Require(AuditRetentionPolicy.TryGetExpiry(from, days, out var expiry), "invalid_retention", 400);
        return expiry;
    }

    private static Instant Min(Instant a, Instant b) => a < b ? a : b;

    private static Instant? MinExpiry(Instant? a, Instant? b)
    {
        if (a == null)
        {
            return b;
        }
        return b == null ? a : Min(a.Value, b.Value);
    }

    private static void Require(
        [System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition,
        string code,
        int status
    )
    {
        if (!condition)
        {
            throw new AuditStoreException(code, status);
        }
    }
}
