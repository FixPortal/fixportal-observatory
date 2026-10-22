using NodaTime;

namespace AiObservatory.Data.Audit;

internal sealed class AuditEvidenceCapture
{
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }
    public Guid MachineId { get; set; }
    public string Generation { get; set; } = "";
    public byte[] Manifest { get; set; } = [];
    public string ManifestHash { get; set; } = "";
    public string SealKey { get; set; } = "";
    public Instant SealedAt { get; set; }
    public Instant? ExpiresAt { get; set; }
    public Instant? DeletedAt { get; set; }
    public long Revision { get; set; }
}
