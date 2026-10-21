using NodaTime;

namespace AiObservatory.Data.Entities;

public sealed class CavemanSession
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string SessionId { get; init; } = "";
    public Instant OccurredAt { get; init; }
    public string? Mode { get; init; }
    public string? Model { get; init; }
    public long OutputTokens { get; init; }
    public long EstSavedTokens { get; init; }
    public decimal EstSavedUsd { get; init; }
}
