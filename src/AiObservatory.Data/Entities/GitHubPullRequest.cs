using NodaTime;

namespace AiObservatory.Data.Entities;

public sealed class GitHubPullRequest
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Repo { get; init; } = "";
    public int Number { get; init; }
    public string Title { get; init; } = "";
    public string Author { get; init; } = "";
    public string State { get; init; } = ""; // open / merged / closed
    public Instant CreatedAt { get; init; }
    public Instant? MergedAt { get; init; }
    public Instant? ClosedAt { get; init; }
    public Instant? FirstReviewAt { get; init; }
    public int ReviewCount { get; init; }
    public Instant IngestedAt { get; init; }
}
