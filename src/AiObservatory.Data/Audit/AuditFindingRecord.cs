namespace AiObservatory.Data.Audit;

internal sealed class AuditFindingRecord
{
    public Guid RunId { get; set; }
    public int Index { get; set; }
    public byte[] Finding { get; set; } = [];
}
