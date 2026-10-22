using NodaTime;

namespace AiObservatory.Data.Audit;

internal sealed class AuditMachineCredential
{
    public Guid Id { get; set; }
    public Guid MachineId { get; set; }
    public byte[] SecretHash { get; set; } = [];
    public Instant CreatedAt { get; set; }
    public Instant? RevokedAt { get; set; }
}
