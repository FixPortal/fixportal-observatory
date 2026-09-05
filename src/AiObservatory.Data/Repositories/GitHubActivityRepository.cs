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
            ? new GitHubBackfillStatus(false, false, false)
            : new GitHubBackfillStatus(state.HasPullRequests, state.HasCommits, state.HasWorkflowRuns);
    }

    public Task MarkBackfillCompletedAsync(string repo, GitHubActivityKind kind, CancellationToken ct = default)
    {
        var hasPullRequests = kind == GitHubActivityKind.PullRequests;
        var hasCommits = kind == GitHubActivityKind.Commits;
        var hasWorkflowRuns = kind == GitHubActivityKind.WorkflowRuns;
        return ctx.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "GitHubBackfillStates" ("Repo", "HasPullRequests", "HasCommits", "HasWorkflowRuns")
            VALUES ({Truncate(repo, 200)}, {hasPullRequests}, {hasCommits}, {hasWorkflowRuns})
            ON CONFLICT ("Repo") DO UPDATE SET
                "HasPullRequests" = "GitHubBackfillStates"."HasPullRequests" OR EXCLUDED."HasPullRequests",
                "HasCommits" = "GitHubBackfillStates"."HasCommits" OR EXCLUDED."HasCommits",
                "HasWorkflowRuns" = "GitHubBackfillStates"."HasWorkflowRuns" OR EXCLUDED."HasWorkflowRuns"
            """,
            ct
        );
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
