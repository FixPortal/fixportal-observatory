using NodaTime;

namespace AiObservatory.Data.Audit;

internal sealed class AuditMachine
{
    public Guid Id { get; set; }
    public Instant CreatedAt { get; set; }
}
