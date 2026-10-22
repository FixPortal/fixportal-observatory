using NodaTime;

namespace AiObservatory.Data.Audit;

internal sealed class AuditServerSnapshot
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public long Revision { get; set; }
    public byte[] Bytes { get; set; } = [];
    public string InputContentDigest { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public Instant FrozenAt { get; set; }
    public Instant? ExpiresAt { get; set; }
}
