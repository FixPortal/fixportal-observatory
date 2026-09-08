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

public record GitHubBackfillStatus(bool HasPullRequests, bool HasCommits, bool HasWorkflowRuns, bool HasReviews);

public enum GitHubActivityKind
{
    PullRequests,
    Commits,
    WorkflowRuns,
    Reviews,
}

public interface IGitHubActivityRepository
{
    Task UpsertPullRequestAsync(GitHubPullRequestRecord record, Instant ingestedAt, CancellationToken ct = default);
    Task UpsertPullRequestReviewAsync(
        GitHubPullRequestReviewRecord record,
        Instant ingestedAt,
        CancellationToken ct = default
    );

    /// <summary>
    /// Writes a pull request and the reviews it carries in one transaction, so the two can never
    /// be torn apart. Prefer this over calling the two single-row upserts in a loop.
    /// </summary>
    Task UpsertPullRequestWithReviewsAsync(
        GitHubPullRequestRecord record,
        Instant ingestedAt,
        CancellationToken ct = default
    );
    Task UpsertCommitAsync(GitHubCommitRecord record, Instant ingestedAt, CancellationToken ct = default);
    Task UpsertWorkflowRunAsync(GitHubWorkflowRunRecord record, Instant ingestedAt, CancellationToken ct = default);
    Task<GitHubBackfillStatus> GetBackfillStatusAsync(string repo, CancellationToken ct = default);
    Task MarkBackfillCompletedAsync(string repo, GitHubActivityKind kind, CancellationToken ct = default);
}
