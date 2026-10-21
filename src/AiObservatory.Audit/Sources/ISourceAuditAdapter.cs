using AiObservatory.Audit.Contracts;

namespace AiObservatory.Audit.Sources;

public interface ISourceAuditAdapter
{
    SourceReconstruction Reconstruct(IReadOnlyList<SourceUsageRecord> records, AuditScope scope);
}
