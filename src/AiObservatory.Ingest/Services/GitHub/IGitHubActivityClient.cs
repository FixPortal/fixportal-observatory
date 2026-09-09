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
    Task<GitHubWorkflowRunResult> GetWorkflowRunsAsync(string repo, LocalDate since, CancellationToken ct = default);
}

/// <param name="Truncated">
/// True when the pagination cap stopped the listing and the backwards window walk could not
/// narrow it further (a cap's worth of runs sharing one created_at second). The caller must
/// not mark backfill complete on a truncated result — the capped runs would never be fetched.
/// </param>
public sealed record GitHubWorkflowRunResult(IReadOnlyList<GitHubWorkflowRunRecord> Runs, bool Truncated);
