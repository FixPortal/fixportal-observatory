using System.Net;
using System.Net.Http.Json;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace AiObservatory.Api.IntegrationTests;

/// <summary>
/// GET /api/github/commits/summary regression coverage. The endpoint 500'd in production —
/// EF Core could not translate a GroupBy/Select projecting straight into the
/// GitHubCommitSummaryResponse record with two Sum aggregates (InvalidOperationException at
/// request time, not startup). The sibling /github/prs and /github/ci routes happened to
/// avoid the same translation trap and the helper-method unit tests never call the query
/// itself, so nothing caught this before it shipped — only a real-Postgres WAF test does.
/// </summary>
[Trait("Category", "Integration")]
[Collection("ApiFactory")]
public class GitHubActivityEndpointsWafTests(AiObservatoryApiFactory factory)
{
    [Fact]
    public async Task GetCommitsSummary_AggregatesAdditionsAndDeletionsPerRepo()
    {
        // Unique out-of-range window (year 2019) so this test's own rows are unambiguously
        // identifiable regardless of what other tests in the shared collection have added.
        var committedAt = Instant.FromUtc(2019, 5, 29, 12, 0);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            db.GitHubCommits.AddRange(
                new GitHubCommit
                {
                    Repo = "FixPortal/waf-commit-summary-test",
                    Sha = "a1",
                    Author = "chris",
                    CommittedAt = committedAt,
                    Additions = 10,
                    Deletions = 2,
                    IngestedAt = committedAt,
                },
                new GitHubCommit
                {
                    Repo = "FixPortal/waf-commit-summary-test",
                    Sha = "a2",
                    Author = "chris",
                    CommittedAt = committedAt,
                    Additions = 5,
                    Deletions = 1,
                    IngestedAt = committedAt,
                }
            );
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var client = factory.CreateAdminClient();
        var response = await client.GetAsync(
            "/api/github/commits/summary?from=2019-05-29&to=2019-05-29",
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var summaries = await response.Content.ReadFromJsonAsync<List<GitHubCommitSummaryRow>>(
            TestContext.Current.CancellationToken
        );
        var row = summaries.Should().ContainSingle(s => s.Repo == "FixPortal/waf-commit-summary-test").Which;
        row.CommitCount.Should().Be(2);
        row.Additions.Should().Be(15);
        row.Deletions.Should().Be(3);
    }

    /// <summary>
    /// The repo allowlist must match the casing the INGEST WORKER writes, not the casing the
    /// allowlist config happens to use. <c>GitHubIngestionService</c> normalises every repo
    /// with <c>ToLowerInvariant</c>, so rows land as "fixportal/x" while
    /// <c>AllowedProjectOwners</c> holds the display form "FixPortal" — and an ordinal
    /// comparison between those matches nothing.
    /// <para>
    /// That silently filtered out every ingested GitHub row in production. It survived
    /// because the worker had never started in Azure, so there was nothing to drop, and
    /// because the test above seeds "FixPortal/..." by hand — encoding the filter's own
    /// assumption instead of the producer's real output. This test seeds what the worker
    /// actually writes.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("fixportal/waf-casing-test")] // what the worker writes
    [InlineData("FixPortal/waf-casing-test")] // display casing, must keep working
    public async Task GetCommitsSummary_MatchesTheRepoAllowlistRegardlessOfCase(string repo)
    {
        var committedAt = Instant.FromUtc(2019, 6, 14, 12, 0);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            db.GitHubCommits.Add(
                new GitHubCommit
                {
                    Repo = repo,
                    Sha = $"case-{Guid.NewGuid():N}",
                    Author = "chris",
                    CommittedAt = committedAt,
                    Additions = 7,
                    Deletions = 3,
                    IngestedAt = committedAt,
                }
            );
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var client = factory.CreateAdminClient();
        var response = await client.GetAsync(
            "/api/github/commits/summary?from=2019-06-14&to=2019-06-14",
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var summaries = await response.Content.ReadFromJsonAsync<List<GitHubCommitSummaryRow>>(
            TestContext.Current.CancellationToken
        );

        summaries
            .Should()
            .Contain(
                s => s.Repo == repo,
                "a repo owned by an allowlisted org must survive the filter whatever its casing"
            );
    }

    /// <summary>A repo outside the allowlist must still be excluded — the fix widens casing, not scope.</summary>
    [Fact]
    public async Task GetCommitsSummary_StillExcludesRepositoriesOutsideTheAllowlist()
    {
        var committedAt = Instant.FromUtc(2019, 6, 15, 12, 0);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            db.GitHubCommits.Add(
                new GitHubCommit
                {
                    Repo = "someoneelse/not-ours",
                    Sha = $"out-{Guid.NewGuid():N}",
                    Author = "chris",
                    CommittedAt = committedAt,
                    Additions = 1,
                    Deletions = 1,
                    IngestedAt = committedAt,
                }
            );
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var client = factory.CreateAdminClient();
        var response = await client.GetAsync(
            "/api/github/commits/summary?from=2019-06-15&to=2019-06-15",
            TestContext.Current.CancellationToken
        );

        var summaries = await response.Content.ReadFromJsonAsync<List<GitHubCommitSummaryRow>>(
            TestContext.Current.CancellationToken
        );

        summaries.Should().NotContain(s => s.Repo == "someoneelse/not-ours");
    }

    /// <summary>
    /// /github/reviews joins reviews to their pull requests and then groups in memory. The join
    /// runs alongside the correlated EXISTS the repo allowlist compiles to — the exact
    /// combination that made /github/commits/summary 500 at request time — so only a
    /// real-Postgres call proves it translates.
    /// </summary>
    [Fact]
    public async Task GetReviews_GroupsByReviewerAndAveragesFirstReviewPerPullRequest()
    {
        const string repo = "FixPortal/waf-reviews-test";
        var openedAt = Instant.FromUtc(2019, 7, 10, 9, 0);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            db.GitHubPullRequests.AddRange(NewPullRequest(repo, 1, openedAt), NewPullRequest(repo, 2, openedAt));
            db.GitHubPullRequestReviews.AddRange(
                // PR 1: the agent reviews after 1h, then again after 3h. Only the first counts
                // toward turnaround; both count toward ReviewCount.
                NewReview(repo, 1, 9001, "coderabbitai[bot]", true, "CHANGES_REQUESTED", openedAt.Plus(Hours(1))),
                NewReview(repo, 1, 9002, "coderabbitai[bot]", true, "APPROVED", openedAt.Plus(Hours(3))),
                // PR 2: the agent reviews after 3h, so its mean across two PRs is 2h.
                NewReview(repo, 2, 9003, "coderabbitai[bot]", true, "APPROVED", openedAt.Plus(Hours(3))),
                NewReview(repo, 1, 9004, "chris", false, "APPROVED", openedAt.Plus(Hours(10)))
            );
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var client = factory.CreateAdminClient();
        var response = await client.GetAsync(
            "/api/github/reviews?from=2019-07-10&to=2019-07-10",
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await response.Content.ReadFromJsonAsync<List<GitHubReviewerRow>>(
            TestContext.Current.CancellationToken
        );

        var agent = rows.Should().ContainSingle(r => r.Repo == repo && r.Reviewer == "coderabbitai[bot]").Which;
        agent.IsBot.Should().BeTrue();
        agent.ReviewCount.Should().Be(3);
        agent.PullRequestCount.Should().Be(2);
        agent.ApprovedCount.Should().Be(2);
        agent.ChangesRequestedCount.Should().Be(1);
        agent.AvgFirstReviewHours.Should().Be(2.0);

        var human = rows.Should().ContainSingle(r => r.Repo == repo && r.Reviewer == "chris").Which;
        human.IsBot.Should().BeFalse();
        human.ReviewCount.Should().Be(1);
        human.AvgFirstReviewHours.Should().Be(10.0);
    }

    /// <summary>A review that was never submitted has no timestamp to place in any range.</summary>
    [Fact]
    public async Task GetReviews_ExcludesPendingReviews()
    {
        const string repo = "FixPortal/waf-reviews-pending-test";
        var openedAt = Instant.FromUtc(2019, 7, 11, 9, 0);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            db.GitHubPullRequests.Add(NewPullRequest(repo, 1, openedAt));
            db.GitHubPullRequestReviews.Add(NewReview(repo, 1, 9101, "chris", false, "PENDING", null));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var client = factory.CreateAdminClient();
        var response = await client.GetAsync(
            "/api/github/reviews?from=2019-07-11&to=2019-07-11",
            TestContext.Current.CancellationToken
        );

        var rows = await response.Content.ReadFromJsonAsync<List<GitHubReviewerRow>>(
            TestContext.Current.CancellationToken
        );
        rows.Should().NotContain(r => r.Repo == repo);
    }

    [Fact]
    public async Task GetReviews_ExcludesRepositoriesOutsideTheAllowlist()
    {
        const string repo = "someoneelse/not-ours";
        var openedAt = Instant.FromUtc(2019, 7, 12, 9, 0);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            db.GitHubPullRequests.Add(NewPullRequest(repo, 1, openedAt));
            db.GitHubPullRequestReviews.Add(
                NewReview(repo, 1, 9201, "coderabbitai[bot]", true, "APPROVED", openedAt.Plus(Hours(1)))
            );
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var client = factory.CreateAdminClient();
        var response = await client.GetAsync(
            "/api/github/reviews?from=2019-07-12&to=2019-07-12",
            TestContext.Current.CancellationToken
        );

        var rows = await response.Content.ReadFromJsonAsync<List<GitHubReviewerRow>>(
            TestContext.Current.CancellationToken
        );
        rows.Should().NotContain(r => r.Repo == repo);
    }

    private static Duration Hours(int hours) => Duration.FromHours(hours);

    private static GitHubPullRequest NewPullRequest(string repo, int number, Instant createdAt) =>
        new()
        {
            Repo = repo,
            Number = number,
            Title = $"waf {number}",
            Author = "chris",
            State = "open",
            CreatedAt = createdAt,
            IngestedAt = createdAt,
        };

    private static GitHubPullRequestReview NewReview(
        string repo,
        int number,
        long reviewId,
        string reviewer,
        bool isBot,
        string state,
        Instant? submittedAt
    ) =>
        new()
        {
            Repo = repo,
            Number = number,
            ReviewId = reviewId,
            Reviewer = reviewer,
            IsBot = isBot,
            State = state,
            SubmittedAt = submittedAt,
            IngestedAt = Instant.FromUtc(2019, 7, 10, 9, 0),
        };

    private sealed record GitHubCommitSummaryRow(string Repo, int CommitCount, int Additions, int Deletions);

    private sealed record GitHubReviewerRow(
        string Repo,
        string Reviewer,
        bool IsBot,
        int ReviewCount,
        int PullRequestCount,
        int ApprovedCount,
        int ChangesRequestedCount,
        double AvgFirstReviewHours
    );
}
