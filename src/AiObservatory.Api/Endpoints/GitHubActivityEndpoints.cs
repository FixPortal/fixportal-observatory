using System.Linq.Expressions;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace AiObservatory.Api.Endpoints;

// Response record properties are consumed by ASP.NET Core JSON serialization.
// ReSharper disable NotAccessedPositionalProperty.Global

public static class GitHubActivityEndpoints
{
    private static readonly string[] TerminalFailureStatuses = ["failure", "timed_out", "startup_failure"];

    // Owners lowercased once here, and the column lowercased in SQL below, because this
    // comparison MUST be case-insensitive — unlike ActivityEndpoints' ordinal one.
    //
    // Two different domains. A Claude session's Project comes from a folder path, where
    // case is meaningful and ordinal is correct. A GitHub owner/repo is case-insensitive by
    // definition, and GitHubIngestionService deliberately normalises it with
    // ToLowerInvariant before writing, so every stored Repo is lowercase while
    // AllowedProjectOwners carries the display casing "FixPortal". Comparing those
    // ordinally matched nothing: "fixportal/x".StartsWith("FixPortal/") is false.
    //
    // That filtered out EVERY ingested GitHub row. It stayed invisible because the ingest
    // worker had never once started in Azure (it failed App Service's startup probe), so
    // the read path had nothing to drop — and the only test seeded "FixPortal/..." by hand,
    // encoding the filter's assumption rather than the producer's actual output.
    private static readonly string[] AllowedRepoOwners =
    [
        .. ActivityEndpoints.AllowedProjectOwners.Select(o => o.ToLowerInvariant()),
    ];

    // Same allowlist rule as ActivityEndpoints.IsAllowedProjectPredicate, but PRs/
    // commits/CI runs are three unrelated entity types (no shared interface) that each
    // expose a plain string Repo — so the one shared predicate body is spliced onto
    // each entity's own Repo access via IsAllowedRepo<T> rather than duplicated per query.
    // ToLower(), NOT ToLowerInvariant(), and the asymmetry with AllowedRepoOwners above is
    // deliberate. The array is built in memory, where ToLowerInvariant is right. This
    // expression is translated to SQL, and EF Core has no translation for
    // ToLowerInvariant — swapping it in makes all three /github routes throw
    // InvalidOperationException at request time (500), which the WAF tests catch. Npgsql
    // renders ToLower() as LOWER(), which is byte/ordinal on the ASCII that GitHub
    // owner/repo names are limited to, so the culture-sensitivity ToLower() implies in
    // memory never reaches the database.
    //
    // ToLower appears twice rather than being hoisted into a local because an expression
    // tree cannot contain a statement body — there is nowhere to put one. EF emits
    // LOWER("Repo") per occurrence: two per owner, over the TWO owners in
    // AllowedRepoOwners, so four per row scanned. Not worth contorting the shape for.
    private static readonly Expression<Func<string, bool>> RepoAllowedTemplate = repo =>
        AllowedRepoOwners.Any(o => repo.ToLower() == o || repo.ToLower().StartsWith(o + "/"));

    private static Expression<Func<T, bool>> IsAllowedRepo<T>(Expression<Func<T, string>> repoSelector)
    {
        var body = new ReplaceParameterVisitor(RepoAllowedTemplate.Parameters[0], repoSelector.Body).Visit(
            RepoAllowedTemplate.Body
        );
        return Expression.Lambda<Func<T, bool>>(body, repoSelector.Parameters[0]);
    }

    private sealed class ReplaceParameterVisitor(ParameterExpression from, Expression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }

    public static void MapGitHubActivityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/github/prs",
                async (AiObservatoryDbContext db, IClock clock, string? from, string? to, CancellationToken ct) =>
                {
                    var today = clock.GetCurrentInstant().InUtc().Date;
                    if (
                        !ActivityEndpoints.TryParseDateRange(from, to, today, out var start, out var end, out var error)
                    )
                    {
                        return error!;
                    }
                    var startInstant = start.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();
                    var endInstant = end.PlusDays(1).AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();

                    var prs = await db
                        .GitHubPullRequests.AsNoTracking()
                        .Where(IsAllowedRepo<GitHubPullRequest>(p => p.Repo))
                        .Where(p =>
                            p.CreatedAt >= startInstant && p.CreatedAt < endInstant
                            || p.MergedAt != null && p.MergedAt >= startInstant && p.MergedAt < endInstant
                            || p.FirstReviewAt != null
                                && p.FirstReviewAt >= startInstant
                                && p.FirstReviewAt < endInstant
                        )
                        .OrderByDescending(p => p.CreatedAt)
                        .ToListAsync(ct);

                    var response = prs.Select(p => new GitHubPrResponse(
                        p.Repo,
                        p.Number,
                        p.Title,
                        p.Author,
                        p.State,
                        p.CreatedAt,
                        p.MergedAt,
                        p.ReviewCount,
                        ComputeTurnaroundHours(p.CreatedAt, p.FirstReviewAt)
                    ));

                    return Results.Ok(response);
                }
            )
            .AddEndpointFilter<AdminOnlyApiKeyEndpointFilter>();

        app.MapGet(
                "/github/reviews",
                async (AiObservatoryDbContext db, IClock clock, string? from, string? to, CancellationToken ct) =>
                {
                    var today = clock.GetCurrentInstant().InUtc().Date;
                    if (
                        !ActivityEndpoints.TryParseDateRange(from, to, today, out var start, out var end, out var error)
                    )
                    {
                        return error!;
                    }
                    var startInstant = start.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();
                    var endInstant = end.PlusDays(1).AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();

                    // Flat rows out of SQL, aggregated in memory. Two reasons, both specific to
                    // this shape: the per-reviewer turnaround needs the FIRST review per PR before
                    // it can be averaged, which is a nested grouping SQL would need a window
                    // function for; and this file has already been bitten twice by EF Core
                    // failing to translate multiple aggregates alongside the correlated EXISTS
                    // that IsAllowedRepo compiles to (see /github/commits/summary above). The row
                    // count is one per submitted review in the range — tens to low hundreds.
                    //
                    // The join is inner: a review whose PR row is absent is excluded. That pairing
                    // is written by GitHubIngestionService in the same loop iteration, so a
                    // reviewed PR always has its row.
                    var rows = await db
                        .GitHubPullRequestReviews.AsNoTracking()
                        .Where(IsAllowedRepo<GitHubPullRequestReview>(r => r.Repo))
                        // Upper bound only, deliberately. A reviewer's FIRST review of a pull
                        // request can sit before the requested range, and turnaround is measured
                        // from that first review — the range selects which activity is REPORTED
                        // (applied in memory below), never which review counts as first.
                        // ponytail: this widens the scan to all history up to `to`. Fine at this
                        // product's scale; if it stops being fine, bound it by the oldest
                        // PullRequests.CreatedAt still in range rather than by SubmittedAt.
                        .Where(r => r.SubmittedAt != null && r.SubmittedAt < endInstant)
                        .Join(
                            db.GitHubPullRequests.AsNoTracking(),
                            r => new { r.Repo, r.Number },
                            p => new { p.Repo, p.Number },
                            (r, p) =>
                                new ReviewerRow(
                                    r.Repo,
                                    r.Reviewer,
                                    r.IsBot,
                                    r.Number,
                                    r.State,
                                    r.SubmittedAt,
                                    p.CreatedAt
                                )
                        )
                        .ToListAsync(ct);

                    var byReviewer = SummariseReviewers(rows, startInstant);

                    return Results.Ok(byReviewer);
                }
            )
            .AddEndpointFilter<AdminOnlyApiKeyEndpointFilter>();

        app.MapGet(
                "/github/commits/summary",
                async (AiObservatoryDbContext db, IClock clock, string? from, string? to, CancellationToken ct) =>
                {
                    var today = clock.GetCurrentInstant().InUtc().Date;
                    if (
                        !ActivityEndpoints.TryParseDateRange(from, to, today, out var start, out var end, out var error)
                    )
                    {
                        return error!;
                    }
                    var startInstant = start.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();
                    var endInstant = end.PlusDays(1).AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();

                    // Projecting straight into the GitHubCommitSummaryResponse record inside the
                    // GroupBy/Select (as the equivalent PR/CI queries do into an anonymous type)
                    // fails to translate here — EF Core cannot turn a record constructor call
                    // carrying three separate group aggregates (Count + two Sums) into SQL when
                    // combined with the correlated EXISTS subquery IsAllowedRepo compiles to, and
                    // throws InvalidOperationException at request time instead of at startup. The
                    // /github/ci query below sidesteps the same trap by materializing into an
                    // anonymous type first and mapping to its response record afterward; mirror
                    // that here.
                    var grouped = await db
                        .GitHubCommits.AsNoTracking()
                        .Where(IsAllowedRepo<GitHubCommit>(c => c.Repo))
                        .Where(c => c.CommittedAt >= startInstant && c.CommittedAt < endInstant)
                        .GroupBy(c => c.Repo)
                        .Select(g => new
                        {
                            Repo = g.Key,
                            CommitCount = g.Count(),
                            Additions = g.Sum(c => c.Additions),
                            Deletions = g.Sum(c => c.Deletions),
                        })
                        .OrderByDescending(r => r.CommitCount)
                        .ToListAsync(ct);

                    var byRepo = grouped
                        .Select(r => new GitHubCommitSummaryResponse(r.Repo, r.CommitCount, r.Additions, r.Deletions))
                        .ToList();

                    return Results.Ok(byRepo);
                }
            )
            .AddEndpointFilter<AdminOnlyApiKeyEndpointFilter>();

        app.MapGet(
                "/github/ci",
                async (AiObservatoryDbContext db, IClock clock, string? from, string? to, CancellationToken ct) =>
                {
                    var today = clock.GetCurrentInstant().InUtc().Date;
                    if (
                        !ActivityEndpoints.TryParseDateRange(from, to, today, out var start, out var end, out var error)
                    )
                    {
                        return error!;
                    }
                    var startInstant = start.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();
                    var endInstant = end.PlusDays(1).AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();

                    var grouped = await db
                        .GitHubWorkflowRuns.AsNoTracking()
                        .Where(IsAllowedRepo<GitHubWorkflowRun>(r => r.Repo))
                        .Where(r => r.CreatedAt >= startInstant && r.CreatedAt < endInstant)
                        .GroupBy(r => new { r.Repo, r.WorkflowName })
                        .Select(g => new
                        {
                            g.Key.Repo,
                            g.Key.WorkflowName,
                            Total = g.Count(),
                            Failed = g.Count(r => Enumerable.Contains(TerminalFailureStatuses, r.Status)),
                            Succeeded = g.Count(r => r.Status == "success"),
                        })
                        .OrderByDescending(r => r.Total)
                        .ToListAsync(ct);

                    var byRepoWorkflow = grouped
                        .Select(r => new GitHubCiResponse(
                            r.Repo,
                            r.WorkflowName,
                            r.Total,
                            r.Failed,
                            ComputeSuccessRate(r.Total, r.Succeeded)
                        ))
                        .ToList();

                    return Results.Ok(byRepoWorkflow);
                }
            )
            .AddEndpointFilter<AdminOnlyApiKeyEndpointFilter>();
    }

    // The flat row /github/reviews projects into. Named rather than anonymous so the aggregation
    // below can be its own method: inlined, it counted against MapGitHubActivityEndpoints' own
    // cognitive complexity, which S3776 caps.
    private sealed record ReviewerRow(
        string Repo,
        string Reviewer,
        bool IsBot,
        int Number,
        string State,
        Instant? SubmittedAt,
        Instant PullRequestCreatedAt
    );

    /// <summary>
    /// Groups per repo, not estate-wide, so this panel answers the same repo filter the PR/commit/CI
    /// panels on the same page do. <paramref name="rows"/> carries every review up to the end of the
    /// range, including ones before <paramref name="startInstant"/>: counts describe the requested
    /// range, but turnaround is measured from the reviewer's first review of each pull request
    /// wherever it falls. A reviewer with no in-range review is not on the panel.
    /// </summary>
    private static List<GitHubReviewerResponse> SummariseReviewers(List<ReviewerRow> rows, Instant startInstant) =>
        rows.GroupBy(r => new
            {
                r.Repo,
                r.Reviewer,
                r.IsBot,
            })
            .Select(g =>
            {
                var inRange = g.Where(r => r.SubmittedAt >= startInstant).ToList();
                if (inRange.Count == 0)
                {
                    return null;
                }
                var turnarounds = inRange
                    .Select(r => r.Number)
                    .Distinct()
                    .Select(number =>
                    {
                        var allForPullRequest = g.Where(r => r.Number == number).ToList();
                        return (
                            allForPullRequest.Min(r => r.SubmittedAt!.Value) - allForPullRequest[0].PullRequestCreatedAt
                        ).TotalHours;
                    })
                    .ToList();
                return new GitHubReviewerResponse(
                    g.Key.Repo,
                    g.Key.Reviewer,
                    g.Key.IsBot,
                    inRange.Count,
                    turnarounds.Count,
                    inRange.Count(r => r.State == "APPROVED"),
                    inRange.Count(r => r.State == "CHANGES_REQUESTED"),
                    Math.Round(turnarounds.Average(), 1)
                );
            })
            .OfType<GitHubReviewerResponse>()
            .OrderByDescending(r => r.ReviewCount)
            .ThenBy(r => r.Repo, StringComparer.Ordinal)
            .ThenBy(r => r.Reviewer, StringComparer.Ordinal)
            .ToList();

    public static double? ComputeTurnaroundHours(Instant createdAt, Instant? firstReviewAt)
    {
        if (firstReviewAt is not { } reviewedAt)
        {
            return null;
        }
        return Math.Round((reviewedAt - createdAt).TotalHours, 1);
    }

    // Only runs with Status == "success" count toward the rate — cancelled/in_progress/queued
    // runs count toward the total but are neither a success nor a (terminal) failure.
    public static double ComputeSuccessRate(int total, int succeeded) =>
        total > 0 ? Math.Round(succeeded * 100.0 / total, 1) : 0;

    public static bool IsTerminalFailure(string status) => TerminalFailureStatuses.Contains(status);
}

public sealed record GitHubPrResponse(
    string Repo,
    int Number,
    string Title,
    string Author,
    string State,
    Instant CreatedAt,
    Instant? MergedAt,
    int ReviewCount,
    double? TurnaroundHours
);

/// <param name="PullRequestCount">Distinct PRs this reviewer submitted at least one review on.</param>
/// <param name="AvgFirstReviewHours">
/// Mean hours from PR open to this reviewer's FIRST review on it — a re-review on the same PR
/// does not drag the figure. Never null: a reviewer only appears here having submitted a review.
/// </param>
public sealed record GitHubReviewerResponse(
    string Repo,
    string Reviewer,
    bool IsBot,
    int ReviewCount,
    int PullRequestCount,
    int ApprovedCount,
    int ChangesRequestedCount,
    double AvgFirstReviewHours
);

public sealed record GitHubCommitSummaryResponse(string Repo, int CommitCount, int Additions, int Deletions);

public sealed record GitHubCiResponse(
    string Repo,
    string WorkflowName,
    int TotalRuns,
    int FailedRuns,
    double SuccessRate
);
