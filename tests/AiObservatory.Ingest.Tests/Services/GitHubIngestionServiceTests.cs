using AiObservatory.Data.Repositories;
using AiObservatory.Ingest.Services.GitHub;
using AiObservatory.Ingest.Sources;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace AiObservatory.Ingest.Tests.Services;

public class GitHubIngestionServiceTests
{
    private static readonly Instant FixedNow = Instant.FromUtc(2026, 7, 1, 12, 0);
    private static readonly FakeClock Clock = new(FixedNow);

    private static IOptions<IngestOptions> Options(params string[] repos) =>
        Microsoft.Extensions.Options.Options.Create(new IngestOptions { GitHubRepoAllowlist = repos });

    private static readonly GitHubBackfillStatus NoPriorData = new(false, false, false, false);
    private static readonly GitHubBackfillStatus FullyBackfilled = new(true, true, true, true);

    [Fact]
    public async Task IngestAsync_UpsertsEveryReviewCarriedByAPullRequest()
    {
        var client = Substitute.For<IGitHubActivityClient>();
        var repo = Substitute.For<IGitHubActivityRepository>();
        repo.GetBackfillStatusAsync("fix-portal/example", Arg.Any<CancellationToken>()).Returns(FullyBackfilled);
        var agentReview = new GitHubPullRequestReviewRecord(
            "fix-portal/example",
            1,
            1001,
            "coderabbitai[bot]",
            true,
            "CHANGES_REQUESTED",
            FixedNow
        );
        var humanReview = new GitHubPullRequestReviewRecord(
            "fix-portal/example",
            1,
            1002,
            "chris",
            false,
            "APPROVED",
            FixedNow
        );
        var pr = new GitHubPullRequestRecord(
            "fix-portal/example",
            1,
            "t",
            "chris",
            "open",
            FixedNow,
            FixedNow,
            null,
            null,
            FixedNow,
            2,
            [agentReview, humanReview]
        );
        client
            .GetPullRequestsAsync("fix-portal/example", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns([pr]);
        client.GetCommitsAsync("fix-portal/example", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        client
            .GetWorkflowRunsAsync(
                "fix-portal/example",
                Arg.Any<LocalDate>(),
                Arg.Any<Instant?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(new GitHubWorkflowRunResult([], false));

        var sut = new GitHubIngestionService(
            client,
            repo,
            Options("fix-portal/example"),
            NullLogger<GitHubIngestionService>.Instance,
            Clock
        );

        var pollDate = new LocalDate(2026, 7, 1);
        await sut.IngestAsync(pollDate, pollDate, TestContext.Current.CancellationToken);

        // The pull request and its reviews go down one transactional call, so the evidence is
        // that the record handed to it carried both reviews.
        await repo.Received(1)
            .UpsertPullRequestWithReviewsAsync(
                Arg.Is<GitHubPullRequestRecord>(r =>
                    r.Reviews != null && r.Reviews.Contains(agentReview) && r.Reviews.Contains(humanReview)
                ),
                FixedNow,
                Arg.Any<CancellationToken>()
            );
    }

    // Reviews defaults to null on the record, and the loop must treat that as "none" rather
    // than throwing — every existing producer path that predates reviews leaves it unset.
    [Fact]
    public async Task IngestAsync_WhenPullRequestCarriesNoReviews_UpsertsNone()
    {
        var client = Substitute.For<IGitHubActivityClient>();
        var repo = Substitute.For<IGitHubActivityRepository>();
        repo.GetBackfillStatusAsync("fix-portal/example", Arg.Any<CancellationToken>()).Returns(FullyBackfilled);
        var pr = new GitHubPullRequestRecord(
            "fix-portal/example",
            1,
            "t",
            "chris",
            "open",
            FixedNow,
            FixedNow,
            null,
            null,
            null,
            0
        );
        client
            .GetPullRequestsAsync("fix-portal/example", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns([pr]);
        client.GetCommitsAsync("fix-portal/example", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        client
            .GetWorkflowRunsAsync(
                "fix-portal/example",
                Arg.Any<LocalDate>(),
                Arg.Any<Instant?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(new GitHubWorkflowRunResult([], false));

        var sut = new GitHubIngestionService(
            client,
            repo,
            Options("fix-portal/example"),
            NullLogger<GitHubIngestionService>.Instance,
            Clock
        );

        var pollDate = new LocalDate(2026, 7, 1);
        await sut.IngestAsync(pollDate, pollDate, TestContext.Current.CancellationToken);

        await repo.Received(1)
            .UpsertPullRequestWithReviewsAsync(
                Arg.Is<GitHubPullRequestRecord>(r => r.Reviews == null || r.Reviews.Count == 0),
                FixedNow,
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task IngestAsync_WhenRepoHasNoPriorData_UsesThirtyDayBackfillWindow()
    {
        var client = Substitute.For<IGitHubActivityClient>();
        var repo = Substitute.For<IGitHubActivityRepository>();
        repo.GetBackfillStatusAsync("fix-portal/example", Arg.Any<CancellationToken>()).Returns(NoPriorData);
        client
            .GetPullRequestsAsync("fix-portal/example", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns([]);
        client.GetCommitsAsync("fix-portal/example", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        client
            .GetWorkflowRunsAsync(
                "fix-portal/example",
                Arg.Any<LocalDate>(),
                Arg.Any<Instant?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(new GitHubWorkflowRunResult([], false));

        var sut = new GitHubIngestionService(
            client,
            repo,
            Options("fix-portal/example"),
            NullLogger<GitHubIngestionService>.Instance,
            Clock
        );

        var pollDate = new LocalDate(2026, 7, 1);
        await sut.IngestAsync(pollDate, pollDate, TestContext.Current.CancellationToken);

        await client
            .Received(1)
            .GetPullRequestsAsync("fix-portal/example", pollDate.PlusDays(-30), Arg.Any<CancellationToken>());
        await repo.Received(1)
            .MarkBackfillCompletedAsync(
                "fix-portal/example",
                GitHubActivityKind.PullRequests,
                Arg.Any<CancellationToken>()
            );
        await repo.Received(1)
            .MarkBackfillCompletedAsync("fix-portal/example", GitHubActivityKind.Commits, Arg.Any<CancellationToken>());
        await repo.Received(1)
            .MarkBackfillCompletedAsync(
                "fix-portal/example",
                GitHubActivityKind.WorkflowRuns,
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task IngestAsync_WhenWorkflowRunsHitThePaginationCap_DoesNotMarkBackfillComplete()
    {
        // A capped listing is silently incomplete; marking it complete would permanently
        // skip every run beyond the cap, so the lane must stay open for the next cycle.
        var client = Substitute.For<IGitHubActivityClient>();
        var repo = Substitute.For<IGitHubActivityRepository>();
        repo.GetBackfillStatusAsync("fix-portal/example", Arg.Any<CancellationToken>()).Returns(NoPriorData);
        client
            .GetPullRequestsAsync("fix-portal/example", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns([]);
        client.GetCommitsAsync("fix-portal/example", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        client
            .GetWorkflowRunsAsync(
                "fix-portal/example",
                Arg.Any<LocalDate>(),
                Arg.Any<Instant?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(new GitHubWorkflowRunResult([], Truncated: true));

        var sut = new GitHubIngestionService(
            client,
            repo,
            Options("fix-portal/example"),
            NullLogger<GitHubIngestionService>.Instance,
            Clock
        );

        await sut.IngestAsync(
            new LocalDate(2026, 7, 1),
            new LocalDate(2026, 7, 1),
            TestContext.Current.CancellationToken
        );

        await repo.Received(1)
            .MarkBackfillCompletedAsync(
                "fix-portal/example",
                GitHubActivityKind.PullRequests,
                Arg.Any<CancellationToken>()
            );
        await repo.Received(1)
            .MarkBackfillCompletedAsync("fix-portal/example", GitHubActivityKind.Commits, Arg.Any<CancellationToken>());
        await repo.DidNotReceive()
            .MarkBackfillCompletedAsync(
                "fix-portal/example",
                GitHubActivityKind.WorkflowRuns,
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task IngestAsync_WhenWorkflowRunsTruncate_PersistsTheResumeCursorInsteadOfMarkingComplete()
    {
        // A truncated walk's cursor is durable backfill state: the next cycle resumes from it
        // instead of re-fetching (and re-burning rate limit on) the same capped windows.
        var cursor = Instant.FromUtc(2026, 6, 20, 9, 0);
        var client = Substitute.For<IGitHubActivityClient>();
        var repo = Substitute.For<IGitHubActivityRepository>();
        repo.GetBackfillStatusAsync("fix-portal/example", Arg.Any<CancellationToken>()).Returns(NoPriorData);
        client
            .GetPullRequestsAsync("fix-portal/example", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns([]);
        client.GetCommitsAsync("fix-portal/example", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        client
            .GetWorkflowRunsAsync(
                "fix-portal/example",
                Arg.Any<LocalDate>(),
                Arg.Any<Instant?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(new GitHubWorkflowRunResult([], Truncated: true, cursor));

        var sut = new GitHubIngestionService(
            client,
            repo,
            Options("fix-portal/example"),
            NullLogger<GitHubIngestionService>.Instance,
            Clock
        );

        await sut.IngestAsync(
            new LocalDate(2026, 7, 1),
            new LocalDate(2026, 7, 1),
            TestContext.Current.CancellationToken
        );

        await repo.Received(1).SaveWorkflowRunsCursorAsync("fix-portal/example", cursor, Arg.Any<CancellationToken>());
        await repo.DidNotReceive()
            .MarkBackfillCompletedAsync(
                "fix-portal/example",
                GitHubActivityKind.WorkflowRuns,
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task IngestAsync_WithAStoredWorkflowRunsCursor_ResumesTheWalkAndClearsOnCompletion()
    {
        var stored = Instant.FromUtc(2026, 6, 20, 9, 0);
        var client = Substitute.For<IGitHubActivityClient>();
        var repo = Substitute.For<IGitHubActivityRepository>();
        repo.GetBackfillStatusAsync("fix-portal/example", Arg.Any<CancellationToken>())
            .Returns(
                new GitHubBackfillStatus(
                    HasPullRequests: true,
                    HasCommits: true,
                    HasWorkflowRuns: false,
                    HasReviews: true,
                    WorkflowRunsCursor: stored
                )
            );
        client
            .GetPullRequestsAsync("fix-portal/example", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns([]);
        client.GetCommitsAsync("fix-portal/example", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        client
            .GetWorkflowRunsAsync(
                "fix-portal/example",
                Arg.Any<LocalDate>(),
                Arg.Any<Instant?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(new GitHubWorkflowRunResult([], Truncated: false));

        var sut = new GitHubIngestionService(
            client,
            repo,
            Options("fix-portal/example"),
            NullLogger<GitHubIngestionService>.Instance,
            Clock
        );

        var pollDate = new LocalDate(2026, 7, 1);
        await sut.IngestAsync(pollDate, pollDate, TestContext.Current.CancellationToken);

        // The stored cursor is the listing start: the walk continues instead of restarting.
        await client
            .Received(1)
            .GetWorkflowRunsAsync("fix-portal/example", pollDate.PlusDays(-30), stored, Arg.Any<CancellationToken>());
        // A completed pass marks the lane (the same upsert clears the cursor repository-side).
        await repo.Received(1)
            .MarkBackfillCompletedAsync(
                "fix-portal/example",
                GitHubActivityKind.WorkflowRuns,
                Arg.Any<CancellationToken>()
            );
        await repo.DidNotReceive()
            .SaveWorkflowRunsCursorAsync(Arg.Any<string>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestAsync_WhenRepoAlreadyHasData_UsesGivenDateNotBackfill()
    {
        var client = Substitute.For<IGitHubActivityClient>();
        var repo = Substitute.For<IGitHubActivityRepository>();
        repo.GetBackfillStatusAsync("fix-portal/example", Arg.Any<CancellationToken>()).Returns(FullyBackfilled);
        client
            .GetPullRequestsAsync("fix-portal/example", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns([]);
        client.GetCommitsAsync("fix-portal/example", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        client
            .GetWorkflowRunsAsync(
                "fix-portal/example",
                Arg.Any<LocalDate>(),
                Arg.Any<Instant?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(new GitHubWorkflowRunResult([], false));

        var sut = new GitHubIngestionService(
            client,
            repo,
            Options("fix-portal/example"),
            NullLogger<GitHubIngestionService>.Instance,
            Clock
        );

        var pollDate = new LocalDate(2026, 7, 1);
        await sut.IngestAsync(pollDate, pollDate, TestContext.Current.CancellationToken);

        await client.Received(1).GetPullRequestsAsync("fix-portal/example", pollDate, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestAsync_WhenOnlyPullRequestsBackfilled_CommitsAndRunsStillGetThirtyDayWindow()
    {
        // Regression case for the bug this fix closes: a repo whose PRs backfilled
        // on an earlier cycle (e.g. before a crash/rate-limit abort) must still get
        // the one-time 30-day backfill for commits and runs — not skip it just
        // because the repo has SOME data.
        var client = Substitute.For<IGitHubActivityClient>();
        var repo = Substitute.For<IGitHubActivityRepository>();
        repo.GetBackfillStatusAsync("fix-portal/example", Arg.Any<CancellationToken>())
            .Returns(
                new GitHubBackfillStatus(
                    HasPullRequests: true,
                    HasCommits: false,
                    HasWorkflowRuns: false,
                    HasReviews: true
                )
            );
        client
            .GetPullRequestsAsync("fix-portal/example", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns([]);
        client.GetCommitsAsync("fix-portal/example", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        client
            .GetWorkflowRunsAsync(
                "fix-portal/example",
                Arg.Any<LocalDate>(),
                Arg.Any<Instant?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(new GitHubWorkflowRunResult([], false));

        var sut = new GitHubIngestionService(
            client,
            repo,
            Options("fix-portal/example"),
            NullLogger<GitHubIngestionService>.Instance,
            Clock
        );

        var pollDate = new LocalDate(2026, 7, 1);
        await sut.IngestAsync(pollDate, pollDate, TestContext.Current.CancellationToken);

        await client.Received(1).GetPullRequestsAsync("fix-portal/example", pollDate, Arg.Any<CancellationToken>());
        await client
            .Received(1)
            .GetCommitsAsync("fix-portal/example", pollDate.PlusDays(-30), Arg.Any<CancellationToken>());
        await client
            .Received(1)
            .GetWorkflowRunsAsync(
                "fix-portal/example",
                pollDate.PlusDays(-30),
                Arg.Any<Instant?>(),
                Arg.Any<CancellationToken>()
            );
    }

    /// <summary>
    /// Reviews arrived after the pull-request backfill had already completed on a live
    /// deployment, so reviews need their own flag. Gating the window on HasPullRequests alone
    /// meant an existing instance never re-fetched: the reviews table was created empty and
    /// only ever filled for pull requests whose updated_at happened to land inside the rolling
    /// window, leaving the historical reviewer roster permanently unreachable while the PR
    /// panel beside it kept showing a full ReviewCount.
    /// </summary>
    [Fact]
    public async Task IngestAsync_WhenPullRequestsBackfilledButReviewsAreNot_RefetchesTheBackfillWindow()
    {
        var client = Substitute.For<IGitHubActivityClient>();
        var repo = Substitute.For<IGitHubActivityRepository>();
        repo.GetBackfillStatusAsync("fix-portal/example", Arg.Any<CancellationToken>())
            .Returns(
                new GitHubBackfillStatus(
                    HasPullRequests: true,
                    HasCommits: true,
                    HasWorkflowRuns: true,
                    HasReviews: false
                )
            );
        client
            .GetPullRequestsAsync("fix-portal/example", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns([]);
        client.GetCommitsAsync("fix-portal/example", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        client
            .GetWorkflowRunsAsync(
                "fix-portal/example",
                Arg.Any<LocalDate>(),
                Arg.Any<Instant?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(new GitHubWorkflowRunResult([], false));

        var sut = new GitHubIngestionService(
            client,
            repo,
            Options("fix-portal/example"),
            NullLogger<GitHubIngestionService>.Instance,
            Clock
        );

        var pollDate = new LocalDate(2026, 7, 1);
        await sut.IngestAsync(pollDate, pollDate, TestContext.Current.CancellationToken);

        // Pull requests carry the reviews, so an unbackfilled reviews lane reopens their window.
        await client
            .Received(1)
            .GetPullRequestsAsync("fix-portal/example", pollDate.PlusDays(-30), Arg.Any<CancellationToken>());
        await repo.Received(1)
            .MarkBackfillCompletedAsync("fix-portal/example", GitHubActivityKind.Reviews, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestAsync_WhenBackfillPersistenceFails_DoesNotMarkThatLaneComplete()
    {
        var client = Substitute.For<IGitHubActivityClient>();
        var repo = Substitute.For<IGitHubActivityRepository>();
        repo.GetBackfillStatusAsync("fix-portal/example", Arg.Any<CancellationToken>()).Returns(NoPriorData);
        var prs = new[]
        {
            new GitHubPullRequestRecord(
                "fix-portal/example",
                1,
                "first",
                "chris",
                "open",
                FixedNow,
                FixedNow,
                null,
                null,
                null,
                0
            ),
            new GitHubPullRequestRecord(
                "fix-portal/example",
                2,
                "second",
                "chris",
                "open",
                FixedNow,
                FixedNow,
                null,
                null,
                null,
                0
            ),
        };
        client
            .GetPullRequestsAsync("fix-portal/example", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(prs);
        repo.UpsertPullRequestWithReviewsAsync(prs[1], FixedNow, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("database unavailable")));

        var sut = new GitHubIngestionService(
            client,
            repo,
            Options("fix-portal/example"),
            NullLogger<GitHubIngestionService>.Instance,
            Clock
        );

        var act = () =>
            sut.IngestAsync(
                new LocalDate(2026, 7, 1),
                new LocalDate(2026, 7, 1),
                TestContext.Current.CancellationToken
            );

        await act.Should().ThrowAsync<InvalidOperationException>();
        await repo.DidNotReceive()
            .MarkBackfillCompletedAsync(
                "fix-portal/example",
                GitHubActivityKind.PullRequests,
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task IngestAsync_PersistsAnOldOpenPrAndUsesItsRecentUpdateAsLatestObservation()
    {
        var client = Substitute.For<IGitHubActivityClient>();
        var repo = Substitute.For<IGitHubActivityRepository>();
        repo.GetBackfillStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(FullyBackfilled);
        var pr = new GitHubPullRequestRecord(
            "fix-portal/example",
            1,
            "t",
            "chris",
            "open",
            Instant.FromUtc(2025, 7, 1, 9, 0),
            Instant.FromUtc(2026, 7, 1, 10, 0),
            null,
            null,
            null,
            0
        );
        client
            .GetPullRequestsAsync(Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns([pr]);
        client.GetCommitsAsync(Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        client
            .GetWorkflowRunsAsync(
                Arg.Any<string>(),
                Arg.Any<LocalDate>(),
                Arg.Any<Instant?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(new GitHubWorkflowRunResult([], false));

        var sut = new GitHubIngestionService(
            client,
            repo,
            Options("fix-portal/example"),
            NullLogger<GitHubIngestionService>.Instance,
            Clock
        );
        var result = await sut.IngestAsync(
            new LocalDate(2026, 7, 1),
            new LocalDate(2026, 7, 2),
            TestContext.Current.CancellationToken
        );

        await repo.Received(1).UpsertPullRequestWithReviewsAsync(pr, FixedNow, Arg.Any<CancellationToken>());
        result.LatestObservationAt.Should().Be(Instant.FromUtc(2026, 7, 1, 10, 0));
    }

    [Fact]
    public async Task IngestAsync_WhenOneRepoThrows403_SkipsItAndAdvancesTheHealthyReposWatermark()
    {
        var client = Substitute.For<IGitHubActivityClient>();
        var repo = Substitute.For<IGitHubActivityRepository>();
        repo.GetBackfillStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(FullyBackfilled);
        client
            .GetPullRequestsAsync("fix-portal/broken", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<GitHubPullRequestRecord>>(new HttpRequestException("403")));
        var pr = new GitHubPullRequestRecord(
            "fix-portal/ok",
            1,
            "t",
            "chris",
            "open",
            Instant.FromUtc(2026, 7, 1, 9, 0),
            Instant.FromUtc(2026, 7, 1, 10, 0),
            null,
            null,
            null,
            0
        );
        client.GetPullRequestsAsync("fix-portal/ok", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([pr]);
        client.GetCommitsAsync(Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        client
            .GetWorkflowRunsAsync(
                Arg.Any<string>(),
                Arg.Any<LocalDate>(),
                Arg.Any<Instant?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(new GitHubWorkflowRunResult([], false));

        var sut = new GitHubIngestionService(
            client,
            repo,
            Options("fix-portal/broken", "fix-portal/ok"),
            NullLogger<GitHubIngestionService>.Instance,
            Clock
        );

        var pollDate = new LocalDate(2026, 7, 1);
        var result = await sut.IngestAsync(pollDate, pollDate, TestContext.Current.CancellationToken);

        // A single flaky repo must not reject the cycle: the healthy repo still ingests and its
        // observation watermark is returned — but the failure rides on the result so the worker
        // records a degraded cycle rather than an unconditional success.
        result.LatestObservationAt.Should().Be(Instant.FromUtc(2026, 7, 1, 10, 0));
        result.FailedRepoCount.Should().Be(1);
        await client
            .Received(1)
            .GetPullRequestsAsync("fix-portal/ok", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestAsync_WhenOneRepoTimesOut_SkipsItAndCompletesTheCycle()
    {
        var client = Substitute.For<IGitHubActivityClient>();
        var repo = Substitute.For<IGitHubActivityRepository>();
        repo.GetBackfillStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(FullyBackfilled);
        client
            .GetPullRequestsAsync("fix-portal/slow", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromException<IReadOnlyList<GitHubPullRequestRecord>>(new TaskCanceledException("client timeout"))
            );
        client.GetPullRequestsAsync("fix-portal/ok", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        client.GetCommitsAsync(Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        client
            .GetWorkflowRunsAsync(
                Arg.Any<string>(),
                Arg.Any<LocalDate>(),
                Arg.Any<Instant?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(new GitHubWorkflowRunResult([], false));

        var sut = new GitHubIngestionService(
            client,
            repo,
            Options("fix-portal/slow", "fix-portal/ok"),
            NullLogger<GitHubIngestionService>.Instance,
            Clock
        );

        var pollDate = new LocalDate(2026, 7, 1);
        var result = await sut.IngestAsync(pollDate, pollDate, TestContext.Current.CancellationToken);

        result.LatestObservationAt.Should().BeNull();
        result.FailedRepoCount.Should().Be(1);
        await client
            .Received(1)
            .GetPullRequestsAsync("fix-portal/ok", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestAsync_WhenCancellationTokenIsCancelled_RethrowsCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var client = Substitute.For<IGitHubActivityClient>();
        var repo = Substitute.For<IGitHubActivityRepository>();
        repo.GetBackfillStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromCanceled<GitHubBackfillStatus>(cts.Token));

        var sut = new GitHubIngestionService(
            client,
            repo,
            Options("fix-portal/example"),
            NullLogger<GitHubIngestionService>.Instance,
            Clock
        );

        // The delegate is awaited before the owning using scope disposes the token source.
        // ReSharper disable once AccessToDisposedClosure
        var act = () => sut.IngestAsync(new LocalDate(2026, 7, 1), new LocalDate(2026, 7, 1), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task IngestAsync_WhenRateLimitExceeded_AbortsRemainingReposAndReportsUnavailable()
    {
        var client = Substitute.For<IGitHubActivityClient>();
        var repo = Substitute.For<IGitHubActivityRepository>();
        repo.GetBackfillStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(FullyBackfilled);
        client
            .GetPullRequestsAsync("fix-portal/first", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromException<IReadOnlyList<GitHubPullRequestRecord>>(new GitHubRateLimitExceededException(10))
            );
        client.GetCommitsAsync(Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        client
            .GetWorkflowRunsAsync(
                Arg.Any<string>(),
                Arg.Any<LocalDate>(),
                Arg.Any<Instant?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(new GitHubWorkflowRunResult([], false));

        var sut = new GitHubIngestionService(
            client,
            repo,
            Options("fix-portal/first", "fix-portal/second"),
            NullLogger<GitHubIngestionService>.Instance,
            Clock
        );

        var pollDate = new LocalDate(2026, 7, 1);
        var act = () => sut.IngestAsync(pollDate, pollDate, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<SourceUnavailableException>();

        await client
            .DidNotReceive()
            .GetPullRequestsAsync("fix-portal/second", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestAsync_WhenRepoAlreadyFailedThenRateLimitHit_ReportsUnavailableAndStops()
    {
        var client = Substitute.For<IGitHubActivityClient>();
        var repo = Substitute.For<IGitHubActivityRepository>();
        repo.GetBackfillStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(FullyBackfilled);
        client
            .GetPullRequestsAsync("fix-portal/broken", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<GitHubPullRequestRecord>>(new HttpRequestException("403")));
        client
            .GetPullRequestsAsync("fix-portal/rate-limited", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromException<IReadOnlyList<GitHubPullRequestRecord>>(new GitHubRateLimitExceededException(10))
            );
        client.GetCommitsAsync(Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        client
            .GetWorkflowRunsAsync(
                Arg.Any<string>(),
                Arg.Any<LocalDate>(),
                Arg.Any<Instant?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(new GitHubWorkflowRunResult([], false));

        var sut = new GitHubIngestionService(
            client,
            repo,
            Options("fix-portal/broken", "fix-portal/rate-limited", "fix-portal/never-reached"),
            NullLogger<GitHubIngestionService>.Instance,
            Clock
        );

        var pollDate = new LocalDate(2026, 7, 1);
        var act = () => sut.IngestAsync(pollDate, pollDate, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<SourceUnavailableException>();

        await client
            .DidNotReceive()
            .GetPullRequestsAsync("fix-portal/never-reached", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestAsync_WhenEveryConfiguredRepositoryFails_RejectsTheWholeSourceAttempt()
    {
        var client = Substitute.For<IGitHubActivityClient>();
        var repo = Substitute.For<IGitHubActivityRepository>();
        repo.GetBackfillStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GitHubBackfillStatus>(new HttpRequestException("403")));
        var sut = new GitHubIngestionService(
            client,
            repo,
            Options("fix-portal/one", "fix-portal/two"),
            NullLogger<GitHubIngestionService>.Instance,
            Clock
        );

        var act = () =>
            sut.IngestAsync(
                new LocalDate(2026, 7, 1),
                new LocalDate(2026, 7, 2),
                TestContext.Current.CancellationToken
            );

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("2 of 2 configured GitHub repos failed*");
    }

    [Fact]
    public async Task IngestAsync_WhenGitHubRateLimits_ReportsTheSourceUnavailable()
    {
        var client = Substitute.For<IGitHubActivityClient>();
        var repo = Substitute.For<IGitHubActivityRepository>();
        repo.GetBackfillStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(FullyBackfilled);
        client
            .GetPullRequestsAsync(Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromException<IReadOnlyList<GitHubPullRequestRecord>>(new GitHubRateLimitExceededException(10))
            );
        var sut = new GitHubIngestionService(
            client,
            repo,
            Options("fix-portal/example"),
            NullLogger<GitHubIngestionService>.Instance,
            Clock
        );

        var act = () =>
            sut.IngestAsync(
                new LocalDate(2026, 7, 1),
                new LocalDate(2026, 7, 2),
                TestContext.Current.CancellationToken
            );

        await act.Should().ThrowAsync<SourceUnavailableException>();
    }
}
