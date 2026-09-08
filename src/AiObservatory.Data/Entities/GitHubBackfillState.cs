namespace AiObservatory.Data.Entities;

public sealed class GitHubBackfillState
{
    public string Repo { get; init; } = "";
    public bool HasPullRequests { get; set; }
    public bool HasCommits { get; set; }
    public bool HasWorkflowRuns { get; set; }

    // Separate from HasPullRequests because the reviews table shipped later: an instance that
    // had already backfilled pull requests must still get one 30-day pass to populate it.
    public bool HasReviews { get; set; }
}
