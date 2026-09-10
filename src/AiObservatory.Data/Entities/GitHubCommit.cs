using NodaTime;

namespace AiObservatory.Data.Entities;

public sealed class GitHubCommit
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Repo { get; init; } = "";
    public string Sha { get; init; } = "";
    public string Author { get; init; } = "";
    public Instant CommittedAt { get; init; }
    public int Additions { get; init; }
    public int Deletions { get; init; }
    public Instant IngestedAt { get; init; }
}
