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
        // Only a total wipe-out rejects the cycle: a single flaky repo among several healthy ones
        // must not trip escalation, and the healthy repos' watermark still advances on partial failure.
        if (result.FailedRepoCount > 0 && result.FailedRepoCount == options.Value.GitHubRepoAllowlist.Length)
        {
            throw new InvalidOperationException(
                $"{result.FailedRepoCount} of {options.Value.GitHubRepoAllowlist.Length} configured GitHub repos failed to ingest this cycle"
            );
        }
        return new SourceIngestionResult(result.LatestObservationAt);
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
                var prs = await client.GetPullRequestsAsync(repo, SinceDate(status.HasPullRequests), cancellationToken);
                foreach (var pr in prs)
                {
                    await repository.UpsertPullRequestAsync(pr, now, cancellationToken);
                    foreach (var review in pr.Reviews ?? [])
                    {
                        await repository.UpsertPullRequestReviewAsync(review, now, cancellationToken);
                    }
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
