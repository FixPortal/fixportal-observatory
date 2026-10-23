using AiObservatory.Data.Audit;
using Microsoft.Extensions.Options;
using NodaTime;

namespace AiObservatory.Api.Audit;

/// <summary>One explicit selected admission and awaited attempt, without queue polling or retry.</summary>
public sealed class AuditSelectedRunService(AuditStore store, AuditWorkerService worker, IOptions<AuditOptions> options)
{
    // "attempt_finished" says the owned pipeline returned. It does not certify a
    // terminal run, publication, or verdict; callers must reconcile persisted truth.
    public async Task<string> ExecuteAsync(
        SelectedRunBinding binding,
        Guid workerId,
        Instant admissionExpiresAt,
        CancellationToken ct
    )
    {
        if (!options.Value.Enabled || options.Value.WorkerEnabled)
        {
            throw new AuditStoreException("selected_execution_disabled", 409);
        }
        var admission = await store.TryAdmitSelectedRunAsync(binding, workerId, admissionExpiresAt, ct);
        if (admission.Lease == null)
        {
            return admission.Status;
        }
        // Store returns a lease only after a known successful commit. Exceptions,
        // including ambiguous commit outcomes, cannot start the leased pipeline.
        await worker.ExecuteLeasedAsync(admission.Lease, ct);
        return "attempt_finished";
    }
}
