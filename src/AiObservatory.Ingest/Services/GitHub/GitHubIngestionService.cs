using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using AiObservatory.Ingest.Sources;
using Microsoft.Extensions.Options;
using NodaTime;

namespace AiObservatory.Ingest.Services.GitHub;

public class GitHubIngestionService(
    IGitHubActivityClient client,
    IGitHubActivityRepository repository,
    IOptions<IngestOptions> options,
    ILogger<GitHubIngestionService> logger,
    IClock clock
) : IUsageSource
{
    private const int BackfillDays = 30;

    public string SourceId => UsageSourceIds.GitHubActivityApi;

    public async Task<SourceIngestionResult> IngestAsync(
        LocalDate from,
        LocalDate through,
        CancellationToken cancellationToken
    )
    {
        _ = through;
        var result = await IngestCoreAsync(from, cancellationToken);
        if (result.RateLimited)
        {
            throw new SourceUnavailableException("GitHub API rate limit exhausted");
        }
        // Only a total wipe-out rejects the cycle outright: a single flaky repo among several
        // healthy ones surfaces as a degraded result instead, so one perma-broken repo cannot
        // starve the healthy lanes of polling — but it is still recorded as a failure, never
        // as a healthy cycle whose advancing watermark would strand the failed lane's window.
        if (result.FailedRepoCount > 0 && result.FailedRepoCount == options.Value.GitHubRepoAllowlist.Length)
        {
            throw new InvalidOperationException(
                $"{result.FailedRepoCount} of {options.Value.GitHubRepoAllowlist.Length} configured GitHub repos failed to ingest this cycle"
            );
        }
        // A partial failure rides on the result so the worker records a degraded cycle: thrown
        // away here, it would read as unconditional success and strand the recovery window.
        return new SourceIngestionResult(result.LatestObservationAt, result.FailedRepoCount);
    }

#pragma warning disable S3776 // One linear per-repository orchestration flow keeps failure policy visible.
    private async Task<GitHubIngestionResult> IngestCoreAsync(LocalDate date, CancellationToken cancellationToken)
    {
        var now = clock.GetCurrentInstant();
        var failedRepoCount = 0;
        Instant? latest = null;
        foreach (var configuredRepo in options.Value.GitHubRepoAllowlist)
        {
            var repo = configuredRepo.ToLowerInvariant();
            try
            {
                var status = await repository.GetBackfillStatusAsync(repo, cancellationToken);
                LocalDate SinceDate(bool hasBackfilled) => hasBackfilled ? date : date.PlusDays(-BackfillDays);
                // Pull requests carry their reviews, so this window reopens when EITHER lane is
                // unbackfilled. Reviews shipped after pull requests: a live instance already has
                // HasPullRequests set, so gating on it alone left the reviews table filling only
                // for pull requests whose updated_at happened to fall inside the rolling window,
                // and the historical reviewer roster was unreachable for good.
                var prs = await client.GetPullRequestsAsync(
                    repo,
                    SinceDate(status.HasPullRequests && status.HasReviews),
                    cancellationToken
                );
                foreach (var pr in prs)
                {
                    // One transaction per pull request: the row and its reviews land together or
                    // not at all. A partial write here would be permanent, because the per-repo
                    // catch below swallows the failure and the watermark still advances.
                    await repository.UpsertPullRequestWithReviewsAsync(pr, now, cancellationToken);
                    latest = Latest(latest, pr.CreatedAt, pr.UpdatedAt, pr.MergedAt, pr.ClosedAt, pr.FirstReviewAt);
                }
                if (!status.HasPullRequests)
                {
                    await repository.MarkBackfillCompletedAsync(
                        repo,
                        GitHubActivityKind.PullRequests,
                        cancellationToken
                    );
                }
                if (!status.HasReviews)
                {
                    await repository.MarkBackfillCompletedAsync(repo, GitHubActivityKind.Reviews, cancellationToken);
                }

                var commits = await client.GetCommitsAsync(repo, SinceDate(status.HasCommits), cancellationToken);
                foreach (var commit in commits)
                {
                    await repository.UpsertCommitAsync(commit, now, cancellationToken);
                    latest = Latest(latest, commit.CommittedAt);
                }
                if (!status.HasCommits)
                {
                    await repository.MarkBackfillCompletedAsync(repo, GitHubActivityKind.Commits, cancellationToken);
                }

                var runs = await client.GetWorkflowRunsAsync(
                    repo,
                    SinceDate(status.HasWorkflowRuns),
                    cancellationToken
                );
                foreach (var run in runs.Runs)
                {
                    await repository.UpsertWorkflowRunAsync(run, now, cancellationToken);
                    latest = Latest(latest, run.CreatedAt);
                }
                // A capped listing is incomplete by definition — marking it complete would
                // permanently skip the runs beyond the cap, so leave the backfill open.
                if (!status.HasWorkflowRuns && !runs.Truncated)
                {
                    await repository.MarkBackfillCompletedAsync(
                        repo,
                        GitHubActivityKind.WorkflowRuns,
                        cancellationToken
                    );
                }

                logger.LogInformation(
                    "GitHub: ingested {PrCount} PRs, {CommitCount} commits, {RunCount} workflow runs for {Repo}",
                    prs.Count,
                    commits.Count,
                    runs.Runs.Count,
                    repo
                );
            }
            catch (GitHubRateLimitExceededException ex)
            {
                logger.LogWarning(ex, "GitHub: aborting remaining repos this poll cycle due to rate limit");
                return new GitHubIngestionResult(failedRepoCount, true, latest);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "GitHub: failed to ingest {Repo}; skipping for this cycle", repo);
                failedRepoCount++;
            }
        }
        return new GitHubIngestionResult(failedRepoCount, false, latest);
    }
#pragma warning restore S3776

    private static Instant? Latest(Instant? current, params Instant?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (candidate is { } value && (current is null || value > current))
            {
                current = value;
            }
        }
        return current;
    }

    private sealed record GitHubIngestionResult(int FailedRepoCount, bool RateLimited, Instant? LatestObservationAt);
}
