using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace AiObservatory.Data.Audit;

public sealed record SelectedCaptureLease(
    string InstanceId,
    Guid RunId,
    long ExpectedRevision,
    string FullScopeDigest,
    Guid RequestId,
    Guid MachineId,
    Guid Token,
    string RequestScopeDigest,
    AuditScope Scope,
    Instant ExpiresAt,
    Instant ServerObservedAt
);

public sealed partial class AuditStore
{
    public Task<SelectedCaptureLease> ClaimSelectedCaptureAsync(
        SelectedRunBinding binding,
        Guid requestId,
        Guid machineId,
        Guid credentialId,
        string requestScopeDigest,
        Instant admissionExpiresAt,
        CancellationToken ct
    ) =>
        SelectedCaptureLeaseAsync(
            binding,
            requestId,
            machineId,
            credentialId,
            requestScopeDigest,
            null,
            admissionExpiresAt,
            ct
        );

    public Task<SelectedCaptureLease> RenewSelectedCaptureAsync(
        SelectedRunBinding binding,
        Guid requestId,
        Guid machineId,
        Guid credentialId,
        Guid leaseToken,
        string requestScopeDigest,
        Instant admissionExpiresAt,
        CancellationToken ct
    ) =>
        SelectedCaptureLeaseAsync(
            binding,
            requestId,
            machineId,
            credentialId,
            requestScopeDigest,
            leaseToken,
            admissionExpiresAt,
            ct
        );

    private async Task<SelectedCaptureLease> SelectedCaptureLeaseAsync(
        SelectedRunBinding binding,
        Guid requestId,
        Guid machineId,
        Guid credentialId,
        string requestScopeDigest,
        Guid? token,
        Instant admissionExpiresAt,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();
        Require(
            binding.RunId != Guid.Empty
                && requestId != Guid.Empty
                && machineId != Guid.Empty
                && credentialId != Guid.Empty
                && token != Guid.Empty
                && binding.ExpectedRevision >= 0
                && IsSelectedDigest(binding.ScopeDigest)
                && IsSelectedDigest(requestScopeDigest),
            "selected_binding_conflict",
            409
        );
        // Transaction resets tracked audit entities; reject first so neither caller work nor
        // unrelated tracked mutations can be discarded or flushed by this exact operation.
        Require(
            !_db
                .ChangeTracker.Entries()
                .Any(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted),
            "selected_dirty_context",
            409
        );
        var lease = await Transaction(
            async () =>
            {
                var now = _clock.GetCurrentInstant();
                Require(now < admissionExpiresAt, "selected_admission_expired", 409);
                var run = await _db.Set<AuditRun>()
                    .AsNoTracking()
                    .SingleOrDefaultAsync(r => r.Id == binding.RunId && r.InstanceId == _instanceId, ct);
                Require(run != null, "selected_binding_conflict", 409);
                RequireSelectedState(run, binding, now);
                Require(
                    run.State == "collecting" && !run.PartialEvidenceAcknowledged,
                    "selected_binding_conflict",
                    409
                );
                var scope = AuditCanonicalJson.Deserialize<AuditScope>(run.Scope);
                var canonical = ValidateScope(scope);
                Require(
                    run.Scope.AsSpan().SequenceEqual(canonical)
                        && AuditCanonicalJson.ContentHash(canonical) == binding.ScopeDigest
                        && run.CaptureRequestIds.Length is > 0 and <= 32
                        && run.CaptureRequestIds.Distinct().Count() == run.CaptureRequestIds.Length
                        && run.CaptureRequestIds.Contains(requestId),
                    "selected_binding_conflict",
                    409
                );
                var request = await _db.Set<AuditCaptureRequest>().SingleOrDefaultAsync(r => r.Id == requestId, ct);
                Require(request != null && request.MachineId == machineId, "selected_capture_conflict", 409);
                var subset = scope with { Sources = scope.Sources.Where(s => s.MachineId == machineId).ToArray() };
                var subsetBytes = AuditCanonicalJson.Serialize(subset);
                Require(
                    subset.Sources.Count > 0
                        && request.Scope.AsSpan().SequenceEqual(subsetBytes)
                        && AuditCanonicalJson.ContentHash(subsetBytes) == requestScopeDigest,
                    "selected_capture_conflict",
                    409
                );
                await RequireCredential(machineId, credentialId, ct);
                Require(
                    !await _db.Set<AuditEvidenceCapture>().AnyAsync(c => c.RequestId == requestId, ct),
                    "selected_capture_conflict",
                    409
                );
                await RequireSelectedCaptureOwner(request, credentialId, token, ct);
                // Reads and the lock wait can cross expiry. Sample again after the last read,
                // immediately before any tracked mutation. Never reclaim or reset old ownership.
                now = _clock.GetCurrentInstant();
                RequireSelectedState(run, binding, now);
                Require(now < admissionExpiresAt && request.Deadline > now, "selected_admission_expired", 409);
                Require(token == null || request.LeaseExpiresAt > now, "capture_lease_expired", 410);
                Require(request.Revision < long.MaxValue, "selected_capture_conflict", 409);
                var expiry = SelectedCaptureExpiry(run, request, admissionExpiresAt, now);
                request.LeaseToken = token ?? Guid.NewGuid();
                request.CredentialId = credentialId;
                request.LeaseExpiresAt = expiry;
                request.State = "uploading";
                request.Revision = checked(request.Revision + 1);
                await _db.SaveChangesAsync(ct);
                return new SelectedCaptureLease(
                    _instanceId,
                    run.Id,
                    binding.ExpectedRevision,
                    binding.ScopeDigest,
                    request.Id,
                    machineId,
                    request.LeaseToken.Value,
                    requestScopeDigest,
                    subset,
                    expiry,
                    default
                );
            },
            ct
        );
        // Only reached after CommitAsync has returned successfully. No retry or inferred
        // rollback after uncertain commit; a later exact claim cannot reset this request.
        ct.ThrowIfCancellationRequested();
        return lease with { ServerObservedAt = _clock.GetCurrentInstant() };
    }

    private static bool IsSelectedDigest(string digest) =>
        digest is { Length: 64 } && digest.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private async Task RequireSelectedCaptureOwner(
        AuditCaptureRequest request,
        Guid credentialId,
        Guid? token,
        CancellationToken ct
    )
    {
        if (token == null)
        {
            Require(
                request.State == "pending"
                    && request.Revision == 0
                    && request.UploadedBytes == 0
                    && request.RecordCount == 0
                    && request.CredentialId == null
                    && request.LeaseToken == null
                    && request.LeaseExpiresAt == null
                    && !await _db.Set<AuditEvidenceChunk>().AnyAsync(c => c.RequestId == request.Id, ct),
                "selected_capture_conflict",
                409
            );
        }
        else
        {
            Require(
                request.State == "uploading"
                    && request.CredentialId == credentialId
                    && request.LeaseToken == token
                    && request.LeaseExpiresAt != null,
                "capture_lease_conflict",
                409
            );
        }
    }

    private Instant SelectedCaptureExpiry(AuditRun run, AuditCaptureRequest request, Instant authority, Instant now)
    {
        var expiry = Min(Min(request.Deadline, authority), now + Duration.FromTimeSpan(_options.RunLeaseDuration));
        if (run.EvidenceExpiresAt is { } evidence)
        {
            expiry = Min(expiry, evidence);
        }
        if (run.SummaryExpiresAt is { } summary)
        {
            expiry = Min(expiry, summary);
        }
        return expiry;
    }
}
