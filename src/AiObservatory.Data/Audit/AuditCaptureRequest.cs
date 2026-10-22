using NodaTime;

namespace AiObservatory.Data.Audit;

internal sealed class AuditCaptureRequest
{
    public Guid Id { get; set; }
    public Guid MachineId { get; set; }
    public byte[] Scope { get; set; } = [];
    public string State { get; set; } = "pending";
    public Instant CreatedAt { get; set; }
    public Instant Deadline { get; set; }
    public Guid? CredentialId { get; set; }
    public Guid? LeaseToken { get; set; }
    public Instant? LeaseExpiresAt { get; set; }
    public long Revision { get; set; }
    public long UploadedBytes { get; set; }
    public long RecordCount { get; set; }
}
