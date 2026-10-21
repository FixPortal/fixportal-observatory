using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace AiObservatory.Data.Repositories;

public class GitHubActivityRepository(AiObservatoryDbContext ctx) : IGitHubActivityRepository
{
    public Task UpsertPullRequestAsync(
        GitHubPullRequestRecord record,
        Instant ingestedAt,
        CancellationToken ct = default
    ) =>
        ctx.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "GitHubPullRequests"
                ("Id", "Repo", "Number", "Title", "Author", "State", "CreatedAt", "MergedAt", "ClosedAt", "FirstReviewAt", "ReviewCount", "IngestedAt")
            VALUES
                ({Guid.NewGuid()}, {Truncate(record.Repo, 200)}, {record.Number}, {Truncate(
                record.Title,
                500
            )}, {Truncate(record.Author, 200)}, {Truncate(
                record.State,
                20
            )}, {record.CreatedAt}, {record.MergedAt}, {record.ClosedAt}, {record.FirstReviewAt}, {record.ReviewCount}, {ingestedAt})
            ON CONFLICT ("Repo", "Number") DO UPDATE SET
                "Title" = EXCLUDED."Title",
                "State" = EXCLUDED."State",
                "MergedAt" = EXCLUDED."MergedAt",
                "ClosedAt" = EXCLUDED."ClosedAt",
                "FirstReviewAt" = COALESCE(EXCLUDED."FirstReviewAt", "GitHubPullRequests"."FirstReviewAt"),
                "ReviewCount" = GREATEST(EXCLUDED."ReviewCount", "GitHubPullRequests"."ReviewCount")
            """,
            ct
        );

    // State and SubmittedAt are both refreshed on conflict: a review submitted after a
    // previous poll saw it PENDING carries a real timestamp the second time, and an approval
    // that is later dismissed changes state without changing its id.
    public Task UpsertPullRequestReviewAsync(
        GitHubPullRequestReviewRecord record,
        Instant ingestedAt,
        CancellationToken ct = default
    ) =>
        ctx.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "GitHubPullRequestReviews"
                ("Id", "Repo", "Number", "ReviewId", "Reviewer", "IsBot", "State", "SubmittedAt", "IngestedAt")
            VALUES
                ({Guid.NewGuid()}, {Truncate(record.Repo, 200)}, {record.Number}, {record.ReviewId}, {Truncate(
                record.Reviewer,
                200
            )}, {record.IsBot}, {Truncate(record.State, 20)}, {record.SubmittedAt}, {ingestedAt})
            ON CONFLICT ("Repo", "ReviewId") DO UPDATE SET
                "State" = EXCLUDED."State",
                "SubmittedAt" = COALESCE(EXCLUDED."SubmittedAt", "GitHubPullRequestReviews"."SubmittedAt")
            """,
            ct
        );

    // The endpoint at /github/reviews inner-joins reviews to their pull request, so a pull request
    // row surviving without its reviews advertises a ReviewCount the review rows cannot account
    // for -- and the join hides the shortfall rather than surfacing it. The ingest loop swallows a
    // per-repo failure while the watermark still advances, so that torn state would never be
    // repaired. One transaction per pull request keeps the pair atomic: on failure, neither lands.
    //
    // Both calls below run on this DbContext, so they enlist in the transaction started here.
    public async Task UpsertPullRequestWithReviewsAsync(
        GitHubPullRequestRecord record,
        Instant ingestedAt,
        CancellationToken ct = default
    )
    {
        await using var transaction = await ctx.Database.BeginTransactionAsync(ct);
        await UpsertPullRequestAsync(record, ingestedAt, ct);
        foreach (var review in record.Reviews ?? [])
        {
            await UpsertPullRequestReviewAsync(review, ingestedAt, ct);
        }
        await transaction.CommitAsync(ct);
    }

    public Task UpsertCommitAsync(GitHubCommitRecord record, Instant ingestedAt, CancellationToken ct = default) =>
        ctx.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "GitHubCommits"
                ("Id", "Repo", "Sha", "Author", "CommittedAt", "Additions", "Deletions", "IngestedAt")
            VALUES
                ({Guid.NewGuid()}, {Truncate(record.Repo, 200)}, {Truncate(
                record.Sha,
                64
            )}, {Truncate(
                record.Author,
                200
            )}, {record.CommittedAt}, {record.Additions}, {record.Deletions}, {ingestedAt})
            ON CONFLICT ("Repo", "Sha") DO NOTHING
            """,
            ct
        );

    public Task UpsertWorkflowRunAsync(
        GitHubWorkflowRunRecord record,
        Instant ingestedAt,
        CancellationToken ct = default
    ) =>
        ctx.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "GitHubWorkflowRuns"
                ("Id", "Repo", "RunId", "WorkflowName", "Status", "CreatedAt", "IngestedAt")
            VALUES
                ({Guid.NewGuid()}, {Truncate(record.Repo, 200)}, {record.RunId}, {Truncate(
                record.WorkflowName,
                200
            )}, {Truncate(record.Status, 20)}, {record.CreatedAt}, {ingestedAt})
            ON CONFLICT ("Repo", "RunId") DO UPDATE SET
                "WorkflowName" = EXCLUDED."WorkflowName",
                "Status" = EXCLUDED."Status"
            """,
            ct
        );

    public async Task<GitHubBackfillStatus> GetBackfillStatusAsync(string repo, CancellationToken ct = default)
    {
        var state = await ctx.GitHubBackfillStates.AsNoTracking().SingleOrDefaultAsync(s => s.Repo == repo, ct);
        return state is null
            ? new GitHubBackfillStatus(false, false, false, false)
            : new GitHubBackfillStatus(
                state.HasPullRequests,
                state.HasCommits,
                state.HasWorkflowRuns,
                state.HasReviews,
                state.WorkflowRunsCursor
            );
    }

    public Task MarkBackfillCompletedAsync(string repo, GitHubActivityKind kind, CancellationToken ct = default)
    {
        var hasPullRequests = kind == GitHubActivityKind.PullRequests;
        var hasCommits = kind == GitHubActivityKind.Commits;
        var hasWorkflowRuns = kind == GitHubActivityKind.WorkflowRuns;
        var hasReviews = kind == GitHubActivityKind.Reviews;
        // The completion flags OR-merge (once backfilled, always backfilled); the workflow-run
        // cursor does NOT merge — a completed run backfill clears it in the same upsert, while
        // an unrelated lane's completion leaves a mid-flight walk's cursor untouched.
        return ctx.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "GitHubBackfillStates" ("Repo", "HasPullRequests", "HasCommits", "HasWorkflowRuns", "HasReviews")
            VALUES ({Truncate(repo, 200)}, {hasPullRequests}, {hasCommits}, {hasWorkflowRuns}, {hasReviews})
            ON CONFLICT ("Repo") DO UPDATE SET
                "HasPullRequests" = "GitHubBackfillStates"."HasPullRequests" OR EXCLUDED."HasPullRequests",
                "HasCommits" = "GitHubBackfillStates"."HasCommits" OR EXCLUDED."HasCommits",
                "HasWorkflowRuns" = "GitHubBackfillStates"."HasWorkflowRuns" OR EXCLUDED."HasWorkflowRuns",
                "HasReviews" = "GitHubBackfillStates"."HasReviews" OR EXCLUDED."HasReviews",
                "WorkflowRunsCursor" = CASE WHEN EXCLUDED."HasWorkflowRuns" THEN NULL ELSE "GitHubBackfillStates"."WorkflowRunsCursor" END
            """,
            ct
        );
    }

    public Task SaveWorkflowRunsCursorAsync(string repo, Instant cursor, CancellationToken ct = default) =>
        ctx.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "GitHubBackfillStates" ("Repo", "HasPullRequests", "HasCommits", "HasWorkflowRuns", "HasReviews", "WorkflowRunsCursor")
            VALUES ({Truncate(repo, 200)}, false, false, false, false, {cursor})
            ON CONFLICT ("Repo") DO UPDATE SET
                "WorkflowRunsCursor" = EXCLUDED."WorkflowRunsCursor"
            """,
            ct
        );

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
