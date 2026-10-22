using AiObservatory.Data.Audit;
using NodaTime;

namespace AiObservatory.Api.Audit;

public sealed class AuditRetentionService(AuditStore store)
{
    public Task<ExpiryCounts> ExpireAsync(Instant now, CancellationToken ct) => store.ExpireAsync(now, ct);

    public Task<DeletionImpact> GetDeletionImpactAsync(Guid captureId, CancellationToken ct) =>
        store.GetDeletionImpactAsync(captureId, ct);

    public async Task<DeleteResult> DeleteCaptureAsync(
        Guid captureId,
        string expectedImpactDigest,
        CancellationToken ct
    )
    {
        await store.DeleteCaptureAsync(captureId, expectedImpactDigest, ct);
        return new(true);
    }

    public async Task DeleteRunAsync(Guid id, CancellationToken ct)
    {
        while (!await store.DeleteRunAsync(id, ct))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
        }
    }
}
