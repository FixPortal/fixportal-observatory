namespace AiObservatory.Data.Audit;

internal sealed class AuditEvidenceChunk
{
    public Guid RequestId { get; set; }
    public int Index { get; set; }
    public string Sha256 { get; set; } = "";
    public byte[] Bytes { get; set; } = [];
    public long RecordCount { get; set; }
}
