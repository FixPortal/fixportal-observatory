using AiObservatory.Audit.Contracts;
using AiObservatory.Data.Audit;
using NodaTime;

namespace AiObservatory.Api.Audit;

public sealed class AuditRunService(AuditStore store, IClock clock)
{
    public Task<Guid> CreateAsync(CreateAuditRunRequest request, string idempotencyKey, CancellationToken ct) =>
        store.CreateRunAsync(
            request.Scope,
            request.CaptureIds,
            idempotencyKey,
            request.RequestFreshCapture,
            clock.GetCurrentInstant(),
            ct
        );

    public async Task<Guid> ReplayAsync(Guid id, string idempotencyKey, CancellationToken ct)
    {
        var selection = await store.ReplaySelectionAsync(id, ct);
        return await CreateAsync(new(selection.Scope, selection.CaptureIds, false), idempotencyKey, ct);
    }
}
