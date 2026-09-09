using AiObservatory.Data.Repositories;
using NodaTime;

namespace AiObservatory.Ingest.Services.GitHub;

public interface IGitHubActivityClient
{
    Task<IReadOnlyList<GitHubPullRequestRecord>> GetPullRequestsAsync(
        string repo,
        LocalDate since,
        CancellationToken ct = default
    );
    Task<IReadOnlyList<GitHubCommitRecord>> GetCommitsAsync(
        string repo,
        LocalDate since,
        CancellationToken ct = default
    );
    Task<GitHubWorkflowRunResult> GetWorkflowRunsAsync(
        string repo,
        LocalDate since,
        Instant? resumeCursor = null,
        CancellationToken ct = default
    );
}

/// <param name="Truncated">
/// True when the pagination cap stopped the listing and the backwards window walk could not
/// narrow it further (a cap's worth of runs sharing one created_at second). The caller must
/// not mark backfill complete on a truncated result — the capped runs would never be fetched.
/// </param>
/// <param name="ResumeCursor">
/// When <paramref name="Truncated"/> is true, how far the backwards window walk got: the
/// oldest run reached. Persisting it lets the next poll cycle resume the walk instead of
/// restarting the same capped windows. Null on a completed listing (nothing left to resume).
/// </param>
public sealed record GitHubWorkflowRunResult(
    IReadOnlyList<GitHubWorkflowRunRecord> Runs,
    bool Truncated,
    Instant? ResumeCursor = null
);
