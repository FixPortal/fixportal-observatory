using NodaTime;

namespace AiObservatory.Data.Entities;

public sealed class ClaudeActivitySession
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string SessionId { get; init; } = "";
    public string Project { get; init; } = "";
    public Instant StartedAt { get; init; }
    public Instant LastSeenAt { get; init; }
    public long ActiveSeconds { get; init; }
    public Instant IngestedAt { get; init; }
}
