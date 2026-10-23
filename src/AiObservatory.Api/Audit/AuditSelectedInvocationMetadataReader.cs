using AiObservatory.Data.Audit;

namespace AiObservatory.Api.Audit;

public interface IAuditSelectedInvocationMetadataReader
{
    Task<SelectedRunMetadata?> ReadAsync(SelectedRunBinding binding, CancellationToken ct);
}

public sealed class AuditSelectedInvocationMetadataReader(AuditStore store) : IAuditSelectedInvocationMetadataReader
{
    public Task<SelectedRunMetadata?> ReadAsync(SelectedRunBinding binding, CancellationToken ct) =>
        store.ReadSelectedRunMetadataAsync(binding, ct);
}
