using AiObservatory.Data.Repositories;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;

namespace AiObservatory.Data.Tests.Repositories;

// Requires TEST_DB_CONNECTION env var pointing at a real PostgreSQL instance.
// Own database (aiobs_test_github), same isolation rationale as AdversarialReviewRepositoryTests.
[Trait("Category", "Integration")]
public class GitHubActivityRepositoryTests : IAsyncLifetime
{
    private string _connStr = null!;
    private AiObservatoryDbContext _ctx = null!;
    private IGitHubActivityRepository _repo = null!;

    public async ValueTask InitializeAsync()
    {
        var baseConn =
            Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
            ?? "Host=localhost;Database=aiobs_test;Username=postgres;Password=postgres";
        _connStr = new NpgsqlConnectionStringBuilder(baseConn)
        {
            Database = $"aiobs_test_github_{Guid.NewGuid():N}",
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(_connStr, o => o.UseNodaTime())
            .Options;
        _ctx = new AiObservatoryDbContext(options);
        await _ctx.Database.MigrateAsync();
        _repo = new GitHubActivityRepository(_ctx);
    }

    public async ValueTask DisposeAsync()
    {
        if (_ctx is not null && _connStr.Contains("_test", StringComparison.OrdinalIgnoreCase))
        {
            await _ctx.Database.EnsureDeletedAsync();
        }
        if (_ctx is not null)
        {
            await _ctx.DisposeAsync();
        }
    }

    private static GitHubPullRequestRecord Pr(
        string state = "open",
        int reviewCount = 0,
        Instant? firstReviewAt = null
    ) =>
        new(
            "fix-portal/example",
            42,
            "Add feature",
            "chris",
            state,
            Instant.FromUtc(2026, 7, 1, 9, 0),
            Instant.FromUtc(2026, 7, 1, 10, 0),
            null,
            null,
            firstReviewAt,
            reviewCount
        );

    [Fact]
    public async Task UpsertPullRequestAsync_WhenNew_Inserts()
    {
        await _repo.UpsertPullRequestAsync(
            Pr(),
            Instant.FromUtc(2026, 7, 1, 10, 0),
            TestContext.Current.CancellationToken
        );

        var stored = await _ctx.GitHubPullRequests.SingleAsync(TestContext.Current.CancellationToken);
        stored.State.Should().Be("open");
        stored.ReviewCount.Should().Be(0);
    }

    [Fact]
    public async Task UpsertPullRequestAsync_WhenRepolledWithNewState_UpdatesInPlace()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repo.UpsertPullRequestAsync(Pr(), Instant.FromUtc(2026, 7, 1, 10, 0), ct);

        await _repo.UpsertPullRequestAsync(
            Pr(state: "merged", reviewCount: 2, firstReviewAt: Instant.FromUtc(2026, 7, 1, 11, 0)),
            Instant.FromUtc(2026, 7, 2, 10, 0),
            ct
        );

        var stored = await _ctx.GitHubPullRequests.SingleAsync(ct);
        stored.State.Should().Be("merged");
        stored.ReviewCount.Should().Be(2);
        stored.FirstReviewAt.Should().Be(Instant.FromUtc(2026, 7, 1, 11, 0));
        // IngestedAt is set only on first insert — a repoll must not disturb it.
        stored.IngestedAt.Should().Be(Instant.FromUtc(2026, 7, 1, 10, 0));
    }

    [Fact]
    public async Task UpsertPullRequestAsync_WhenRepolledWithMissingReviewData_KeepsExistingReviewMetrics()
    {
        var ct = TestContext.Current.CancellationToken;
        var firstReviewAt = Instant.FromUtc(2026, 7, 1, 11, 0);
        await _repo.UpsertPullRequestAsync(
            Pr(reviewCount: 4, firstReviewAt: firstReviewAt),
            Instant.FromUtc(2026, 7, 1, 10, 0),
            ct
        );

        await _repo.UpsertPullRequestAsync(
            Pr(state: "merged", reviewCount: 0, firstReviewAt: null),
            Instant.FromUtc(2026, 7, 2, 10, 0),
            ct
        );

        var stored = await _ctx.GitHubPullRequests.SingleAsync(ct);
        stored.State.Should().Be("merged");
        stored.ReviewCount.Should().Be(4);
        stored.FirstReviewAt.Should().Be(firstReviewAt);
    }

    [Fact]
    public async Task UpsertPullRequestAsync_TruncatesExternalStringsToDatabaseLimits()
    {
        var ct = TestContext.Current.CancellationToken;
        var record = Pr() with
        {
            Repo = new string('r', 250),
            Title = new string('t', 600),
            Author = new string('a', 250),
            State = new string('s', 25),
        };

        await _repo.UpsertPullRequestAsync(record, Instant.FromUtc(2026, 7, 1, 10, 0), ct);

        var stored = await _ctx.GitHubPullRequests.SingleAsync(ct);
        stored.Repo.Should().HaveLength(200);
        stored.Title.Should().HaveLength(500);
        stored.Author.Should().HaveLength(200);
        stored.State.Should().HaveLength(20);
    }

    [Fact]
    public async Task UpsertPullRequestWithReviewsAsync_WritesThePullRequestAndEveryReview()
    {
        var ct = TestContext.Current.CancellationToken;
        var at = Instant.FromUtc(2026, 7, 1, 10, 0);

        await _repo.UpsertPullRequestWithReviewsAsync(
            Pr(reviewCount: 2) with
            {
                Reviews =
                [
                    Review(9001, "coderabbitai[bot]", isBot: true, "CHANGES_REQUESTED"),
                    Review(9002, "chris", isBot: false, "APPROVED"),
                ],
            },
            at,
            ct
        );

        (await _ctx.GitHubPullRequests.CountAsync(ct)).Should().Be(1);
        var reviewers = await _ctx.GitHubPullRequestReviews.Select(r => r.Reviewer).ToListAsync(ct);
        reviewers.Should().BeEquivalentTo("coderabbitai[bot]", "chris");
    }

    /// <summary>
    /// The pair is the contract: /github/reviews inner-joins reviews to their pull request, so a
    /// pull request row that survives without its reviews advertises a ReviewCount the review rows
    /// cannot account for, and the join hides the shortfall instead of surfacing it. Because the
    /// ingest loop swallows a per-repo failure and the watermark still advances, that torn state
    /// would be permanent — so a failed review write must take the pull request row down with it.
    /// </summary>
    [Fact]
    public async Task UpsertPullRequestWithReviewsAsync_WhenAReviewFails_WritesNeitherHalf()
    {
        var ct = TestContext.Current.CancellationToken;
        var at = Instant.FromUtc(2026, 7, 1, 10, 0);

        // A NUL character is rejected by PostgreSQL itself, so the failure lands server-side
        // between the two writes rather than in C# before the first one.
        var poisoned = Review(9002, "ch\0ris", isBot: false, "APPROVED");
        var act = async () =>
            await _repo.UpsertPullRequestWithReviewsAsync(
                Pr(reviewCount: 2) with
                {
                    Reviews = [Review(9001, "coderabbitai[bot]", isBot: true, "APPROVED"), poisoned],
                },
                at,
                ct
            );

        await act.Should().ThrowAsync<Exception>();

        (await _ctx.GitHubPullRequests.CountAsync(ct)).Should().Be(0);
        (await _ctx.GitHubPullRequestReviews.CountAsync(ct)).Should().Be(0);
    }

    private static GitHubPullRequestReviewRecord Review(long reviewId, string reviewer, bool isBot, string state) =>
        new("fix-portal/example", 42, reviewId, reviewer, isBot, state, Instant.FromUtc(2026, 7, 1, 10, 0));

    [Fact]
    public async Task UpsertCommitAsync_WhenRepolled_IsNoOpNotDuplicate()
    {
        var ct = TestContext.Current.CancellationToken;
        var commit = new GitHubCommitRecord(
            "fix-portal/example",
            "abc123",
            "chris",
            Instant.FromUtc(2026, 7, 1, 9, 0),
            10,
            2
        );

        await _repo.UpsertCommitAsync(commit, Instant.FromUtc(2026, 7, 1, 10, 0), ct);
        await _repo.UpsertCommitAsync(commit, Instant.FromUtc(2026, 7, 2, 10, 0), ct);

        var count = await _ctx.GitHubCommits.CountAsync(ct);
        count.Should().Be(1);
    }

    [Fact]
    public async Task UpsertCommitAsync_AcceptsSixtyFourCharacterSha()
    {
        var ct = TestContext.Current.CancellationToken;
        var sha256 = new string('a', 64);
        var commit = new GitHubCommitRecord(
            "fix-portal/example",
            sha256,
            "chris",
            Instant.FromUtc(2026, 7, 1, 9, 0),
            10,
            2
        );

        await _repo.UpsertCommitAsync(commit, Instant.FromUtc(2026, 7, 1, 10, 0), ct);

        var stored = await _ctx.GitHubCommits.SingleAsync(ct);
        stored.Sha.Should().Be(sha256);
    }

    [Fact]
    public async Task UpsertCommitAsync_TruncatesExternalStringsToDatabaseLimits()
    {
        var ct = TestContext.Current.CancellationToken;
        var commit = new GitHubCommitRecord(
            new string('r', 250),
            "abc123",
            new string('a', 250),
            Instant.FromUtc(2026, 7, 1, 9, 0),
            10,
            2
        );

        await _repo.UpsertCommitAsync(commit, Instant.FromUtc(2026, 7, 1, 10, 0), ct);

        var stored = await _ctx.GitHubCommits.SingleAsync(ct);
        stored.Repo.Should().HaveLength(200);
        stored.Author.Should().HaveLength(200);
    }

    [Fact]
    public async Task UpsertWorkflowRunAsync_WhenStatusChanges_UpdatesStatus()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = new GitHubWorkflowRunRecord(
            "fix-portal/example",
            999,
            "ci.yml",
            "in_progress",
            Instant.FromUtc(2026, 7, 1, 9, 0)
        );

        await _repo.UpsertWorkflowRunAsync(run, Instant.FromUtc(2026, 7, 1, 9, 0), ct);
        await _repo.UpsertWorkflowRunAsync(run with { Status = "success" }, Instant.FromUtc(2026, 7, 1, 9, 5), ct);

        var stored = await _ctx.GitHubWorkflowRuns.SingleAsync(ct);
        stored.Status.Should().Be("success");
    }

    [Fact]
    public async Task UpsertWorkflowRunAsync_WhenWorkflowNameChanges_UpdatesWorkflowName()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = new GitHubWorkflowRunRecord(
            "fix-portal/example",
            999,
            "old.yml",
            "in_progress",
            Instant.FromUtc(2026, 7, 1, 9, 0)
        );

        await _repo.UpsertWorkflowRunAsync(run, Instant.FromUtc(2026, 7, 1, 9, 0), ct);
        await _repo.UpsertWorkflowRunAsync(
            run with
            {
                WorkflowName = "new.yml",
                Status = "success",
            },
            Instant.FromUtc(2026, 7, 1, 9, 5),
            ct
        );

        var stored = await _ctx.GitHubWorkflowRuns.SingleAsync(ct);
        stored.WorkflowName.Should().Be("new.yml");
        stored.Status.Should().Be("success");
    }

    [Fact]
    public async Task UpsertWorkflowRunAsync_TruncatesExternalStringsToDatabaseLimits()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = new GitHubWorkflowRunRecord(
            new string('r', 250),
            999,
            new string('w', 250),
            new string('s', 25),
            Instant.FromUtc(2026, 7, 1, 9, 0)
        );

        await _repo.UpsertWorkflowRunAsync(run, Instant.FromUtc(2026, 7, 1, 9, 0), ct);

        var stored = await _ctx.GitHubWorkflowRuns.SingleAsync(ct);
        stored.Repo.Should().HaveLength(200);
        stored.WorkflowName.Should().HaveLength(200);
        stored.Status.Should().HaveLength(20);
    }

    [Fact]
    public async Task GetBackfillStatusAsync_WhenNoRowsForRepo_AllFalse()
    {
        var result = await _repo.GetBackfillStatusAsync("fix-portal/never-seen", TestContext.Current.CancellationToken);
        result.HasPullRequests.Should().BeFalse();
        result.HasCommits.Should().BeFalse();
        result.HasWorkflowRuns.Should().BeFalse();
    }

    [Fact]
    public async Task GetBackfillStatusAsync_WhenRowsExistWithoutCompletionMarker_AllFalse()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repo.UpsertCommitAsync(
            new GitHubCommitRecord("fix-portal/example", "abc123", "chris", Instant.FromUtc(2026, 7, 1, 9, 0), 1, 0),
            Instant.FromUtc(2026, 7, 1, 9, 0),
            ct
        );

        var result = await _repo.GetBackfillStatusAsync("fix-portal/example", ct);
        result.HasPullRequests.Should().BeFalse();
        result.HasCommits.Should().BeFalse();
        result.HasWorkflowRuns.Should().BeFalse();
    }

    [Fact]
    public async Task MarkBackfillCompletedAsync_AdvancesOnlyTheCompletedLane()
    {
        var ct = TestContext.Current.CancellationToken;

        await _repo.MarkBackfillCompletedAsync("fix-portal/example", GitHubActivityKind.Commits, ct);

        var result = await _repo.GetBackfillStatusAsync("fix-portal/example", ct);
        result.HasPullRequests.Should().BeFalse();
        result.HasCommits.Should().BeTrue();
        result.HasWorkflowRuns.Should().BeFalse();
    }
}
