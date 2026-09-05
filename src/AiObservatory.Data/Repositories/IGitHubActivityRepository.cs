using NodaTime;

namespace AiObservatory.Data.Repositories;

public record GitHubPullRequestRecord(
    string Repo,
    int Number,
    string Title,
    string Author,
    string State,
    Instant CreatedAt,
    Instant UpdatedAt,
    Instant? MergedAt,
    Instant? ClosedAt,
    Instant? FirstReviewAt,
    int ReviewCount,
    // Optional so the many call sites that only care about PR-level fields need not
    // construct an empty list. Null and empty both mean "this PR has no reviews".
    IReadOnlyList<GitHubPullRequestReviewRecord>? Reviews = null
);

public record GitHubPullRequestReviewRecord(
    string Repo,
    int Number,
    long ReviewId,
    string Reviewer,
    bool IsBot,
    string State,
    Instant? SubmittedAt
);

public record GitHubCommitRecord(
    string Repo,
    string Sha,
    string Author,
    Instant CommittedAt,
    int Additions,
    int Deletions
);

public record GitHubWorkflowRunRecord(string Repo, long RunId, string WorkflowName, string Status, Instant CreatedAt);

public record GitHubBackfillStatus(bool HasPullRequests, bool HasCommits, bool HasWorkflowRuns);

public enum GitHubActivityKind
{
    PullRequests,
    Commits,
    WorkflowRuns,
}

public interface IGitHubActivityRepository
{
    Task UpsertPullRequestAsync(GitHubPullRequestRecord record, Instant ingestedAt, CancellationToken ct = default);
    Task UpsertPullRequestReviewAsync(
        GitHubPullRequestReviewRecord record,
        Instant ingestedAt,
        CancellationToken ct = default
    );
    Task UpsertCommitAsync(GitHubCommitRecord record, Instant ingestedAt, CancellationToken ct = default);
    Task UpsertWorkflowRunAsync(GitHubWorkflowRunRecord record, Instant ingestedAt, CancellationToken ct = default);
    Task<GitHubBackfillStatus> GetBackfillStatusAsync(string repo, CancellationToken ct = default);
    Task MarkBackfillCompletedAsync(string repo, GitHubActivityKind kind, CancellationToken ct = default);
}
