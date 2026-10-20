using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;

namespace AiObservatory.Data.Tests.Repositories;

// Requires TEST_DB_CONNECTION env var pointing at a real PostgreSQL instance.
// Runs against its OWN database (not the shared aiobs_test) so it never races
// the other integration-test class, which drops its database on dispose. xUnit
// runs separate test classes in parallel, so sharing one database meant one
// class could DROP it mid-migrate in the other (CI: 57P01 / "index ... does
// not exist"). MigrateAsync creates this database on first use.
[Trait("Category", "Integration")]
public class AdversarialReviewRepositoryTests : IAsyncLifetime
{
    private string _connStr = null!;
    private AiObservatoryDbContext _ctx = null!;
    private IAdversarialReviewRepository _repo = null!;

    public async ValueTask InitializeAsync()
    {
        var baseConn =
            Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
            ?? "Host=localhost;Database=aiobs_test;Username=postgres;Password=postgres";
        // Keep the "_test" marker (DisposeAsync only drops a _test database).
        _connStr = new NpgsqlConnectionStringBuilder(baseConn)
        {
            Database = $"aiobs_test_adversarial_{Guid.NewGuid():N}",
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(_connStr, o => o.UseNodaTime())
            .Options;
        _ctx = new AiObservatoryDbContext(options);
        await _ctx.Database.MigrateAsync();
        _repo = new AdversarialReviewRepository(_ctx);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ctx is not null && _connStr.Contains("_test", StringComparison.OrdinalIgnoreCase))
            {
                await _ctx.Database.EnsureDeletedAsync();
            }
        }
        finally
        {
            if (_ctx is not null)
            {
                await _ctx.DisposeAsync();
            }
        }
    }

    private static AdversarialReviewRun Run(
        string runId,
        string reviewer,
        string role,
        string model,
        decimal costUsd = 0.10m,
        int raised = 3,
        int accepted = 2,
        int? chunkCount = null,
        Instant? recordedAt = null,
        bool costUnknown = false,
        decimal? costPerAcceptedFinding = null,
        long tokens = 0
    ) =>
        new()
        {
            InputTokens = tokens,
            OutputTokens = tokens,
            CostUnknown = costUnknown,
            CostPerAcceptedFinding = costPerAcceptedFinding,
            RunId = runId,
            Reviewer = reviewer,
            Role = role,
            Model = model,
            IssuesRaised = role == "judge" ? 0 : raised,
            IssuesAccepted = role == "judge" ? 0 : accepted,
            CostUsd = costUsd,
            ReviewDurationMs = 1000,
            ChunkCount = chunkCount,
            RecordedAt = recordedAt ?? Instant.FromUtc(2026, 6, 27, 12, 0),
        };

    [Fact]
    public async Task Losing_a_confirmed_insert_race_corrects_the_winner_and_keeps_the_context_usable()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(_connStr, o => o.UseNodaTime())
            .Options;
        await using var winnerContext = new AiObservatoryDbContext(options);
        await using var loserContext = new AiObservatoryDbContext(options);
        await winnerContext.Database.OpenConnectionAsync(ct);
        await loserContext.Database.OpenConnectionAsync(ct);
        var winnerPid = await winnerContext
            .Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"")
            .SingleAsync(ct);
        var loserPid = await loserContext
            .Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"")
            .SingleAsync(ct);
        var runId = $"race-{Guid.NewGuid():N}";
        var recordedAt = Instant.FromUtc(2026, 6, 27, 12, 0);
        var winner = new AdversarialReviewRun
        {
            RunId = runId,
            Reviewer = "race-reviewer",
            Role = "reviewer",
            Model = "initial-model",
            Repo = "initial-repo",
            Summary = "initial-summary",
            InputTokens = 10,
            OutputTokens = 20,
            CostUsd = 0.25m,
            ReviewDurationMs = 100,
            IssuesRaised = 1,
            IssuesAccepted = 0,
            CostPerAcceptedFinding = null,
            ChunkCount = null,
            RecordedAt = recordedAt,
        };
        var loser = new AdversarialReviewRun
        {
            RunId = runId,
            Reviewer = winner.Reviewer,
            Role = winner.Role,
            Model = "corrected-model",
            Repo = "corrected-repo",
            Summary = "corrected-summary",
            InputTokens = 111,
            OutputTokens = 222,
            CostUsd = 12m,
            ReviewDurationMs = 3333,
            IssuesRaised = 8,
            IssuesAccepted = 4,
            CostPerAcceptedFinding = 3m,
            ChunkCount = 7,
            RecordedAt = recordedAt + Duration.FromDays(1),
        };
        winner.Id.Should().NotBe(loser.Id);
        var loserRepository = new AdversarialReviewRepository(loserContext);
        var transaction = await winnerContext.Database.BeginTransactionAsync(ct);
        Task<(Guid Id, bool Existed)>? competing = null;
        try
        {
            var inserted = await new AdversarialReviewRepository(winnerContext).RecordRunAsync(winner, ct);
            inserted.Should().Be((winner.Id, false));
            (await _repo.GetRunsAsync(runId, ct)).Should().BeEmpty("the winner is still uncommitted");
            competing = loserRepository.RecordRunAsync(loser, ct);
            await WaitForInsertBlockedByAsync(winnerPid, loserPid, ct);
            await transaction.CommitAsync(ct);
            (Guid Id, bool Existed) result = default;
            var finish = async () =>
            {
                result = await competing;
            };
            await finish.Should().NotThrowAsync("the unique-insert loser must recover after the winner commits");
            result.Existed.Should().BeTrue();
            result.Id.Should().Be(winner.Id);
            result.Id.Should().NotBe(loser.Id);
            var row = (await _repo.GetRunsAsync(runId, ct)).Should().ContainSingle().Which;
            row.Id.Should().Be(winner.Id);
            row.RecordedAt.Should().Be(recordedAt);
            row.Should()
                .BeEquivalentTo(
                    new
                    {
                        loser.Model,
                        loser.Repo,
                        loser.Summary,
                        loser.InputTokens,
                        loser.OutputTokens,
                        loser.CostUsd,
                        loser.ReviewDurationMs,
                        loser.IssuesRaised,
                        loser.IssuesAccepted,
                        loser.CostPerAcceptedFinding,
                        loser.ChunkCount,
                    }
                );

            // A stale Added loser would be inserted again by this public save, poisoning the same context.
            var next = Run(runId + "-after", winner.Reviewer, winner.Role, "next-model", recordedAt: recordedAt);
            (Guid Id, bool Existed) nextResult = default;
            var saveNext = async () =>
            {
                nextResult = await loserRepository.RecordRunAsync(next, ct);
            };
            await saveNext.Should().NotThrowAsync("the recovered context must not retry its failed Added participant");
            nextResult.Should().Be((next.Id, false));
            (await _repo.GetRunsAsync(next.RunId, ct)).Should().ContainSingle().Which.Id.Should().Be(next.Id);
            (await _repo.GetRunsAsync(runId, ct)).Should().ContainSingle().Which.Id.Should().Be(winner.Id);
        }
        finally
        {
            // Release the blocker before draining the competing command, including failed setup/assertions.
            await transaction.DisposeAsync();
            await deadline.CancelAsync();
            if (competing is not null)
            {
                try
                {
                    await competing.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                }
                catch (Exception exception) when (deadline.IsCancellationRequested)
                {
                    Console.WriteLine($"Cancelled loser cleanup: {exception.GetType().Name}");
                }
            }
        }
    }

    private async Task WaitForInsertBlockedByAsync(int winnerPid, int loserPid, CancellationToken ct)
    {
        const string insert = "INSERT INTO \"AdversarialReviewRuns\"";
        // The caller's own 30 s deadline owns the test budget; this helper keeps a shorter
        // one so a wait that never appears fails with a timeout that names the fragment
        // instead of the caller's bare cancellation.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var waitToken = deadline.Token;
        try
        {
            while (true)
            {
                var blocked = await _ctx
                    .Database.SqlQuery<bool>(
                        $"""
                        SELECT EXISTS (
                            SELECT 1 FROM pg_stat_activity
                            WHERE datname = current_database() AND pid = {loserPid}
                                AND wait_event_type = 'Lock'
                                AND query LIKE '%' || {insert} || '%'
                                AND {winnerPid} = ANY(pg_blocking_pids({loserPid}))
                                AND EXISTS (
                                    SELECT 1 FROM pg_locks
                                    WHERE pid = {loserPid} AND locktype = 'transactionid' AND NOT granted
                                )
                        ) AS "Value"
                        """
                    )
                    .SingleAsync(waitToken);
                if (blocked)
                {
                    Console.WriteLine(
                        $"Observed PostgreSQL loser INSERT {loserPid} blocked by uncommitted winner {winnerPid} in {_ctx.Database.GetDbConnection().Database}."
                    );
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(50), waitToken);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException($"No session reached the expected PostgreSQL lock wait for '{insert}'.");
        }
    }

    [Fact]
    public async Task Four_participants_sharing_one_runId_all_persist()
    {
        var ct = TestContext.Current.CancellationToken;
        var id1 = await _repo.RecordRunAsync(Run("R1", "anthropic", "reviewer", "claude-sonnet-4-6"), ct);
        var id2 = await _repo.RecordRunAsync(Run("R1", "google", "reviewer", "gemini-2.5-pro"), ct);
        var id3 = await _repo.RecordRunAsync(Run("R1", "openai", "reviewer", "gpt-5.4"), ct);
        var id4 = await _repo.RecordRunAsync(Run("R1", "anthropic", "judge", "claude-opus-4-8"), ct);

        id1.Existed.Should().BeFalse();
        id2.Existed.Should().BeFalse();
        id3.Existed.Should().BeFalse();
        id4.Existed.Should().BeFalse();
        (await _repo.GetRunsAsync("R1", ct)).Should().HaveCount(4);
    }

    [Fact]
    public async Task Re_emitting_same_participant_updates_in_place()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await _repo.RecordRunAsync(
            Run("R2", "anthropic", "reviewer", "claude-sonnet-4-6", costUsd: 0m, raised: 0, accepted: 0),
            ct
        );
        // Backfill the same participant with real numbers and a chunk count.
        var second = await _repo.RecordRunAsync(
            Run(
                "R2",
                "anthropic",
                "reviewer",
                "claude-sonnet-4-6",
                costUsd: 1.50m,
                raised: 9,
                accepted: 6,
                chunkCount: 4
            ),
            ct
        );

        first.Existed.Should().BeFalse();
        second.Existed.Should().BeTrue();
        second.Id.Should().Be(first.Id); // same row corrected, not a new one

        var rows = (await _repo.GetRunsAsync("R2", ct)).ToList();
        rows.Should().ContainSingle();
        var row = rows[0];
        row.CostUsd.Should().Be(1.50m);
        row.IssuesRaised.Should().Be(9);
        row.IssuesAccepted.Should().Be(6);
        row.ChunkCount.Should().Be(4);
    }

    [Fact]
    public async Task DeleteAllRuns_clears_the_table()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repo.RecordRunAsync(Run("R3", "anthropic", "reviewer", "claude-sonnet-4-6"), ct);
        var deleted = await _repo.DeleteAllRunsAsync(ct);
        deleted.Should().BeGreaterThan(0);
        (await _repo.GetRunsAsync(ct: ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteRun_removes_only_that_runs_participants()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repo.RecordRunAsync(Run("R4", "anthropic", "reviewer", "claude-sonnet-4-6"), ct);
        await _repo.RecordRunAsync(Run("R4", "openai", "reviewer", "gpt-5"), ct);
        await _repo.RecordRunAsync(Run("R5", "anthropic", "reviewer", "claude-sonnet-4-6"), ct);

        var deleted = await _repo.DeleteRunAsync("R4", ct);

        deleted.Should().Be(2);
        (await _repo.GetRunsAsync("R4", ct)).Should().BeEmpty();
        (await _repo.GetRunsAsync("R5", ct)).Should().ContainSingle();
    }

    [Fact]
    public async Task DeleteRun_for_unknown_runId_deletes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        (await _repo.DeleteRunAsync("does-not-exist", ct)).Should().Be(0);
    }

    [Fact]
    public async Task Stats_average_cost_per_finding_is_ratio_of_sums_not_mean_of_ratios()
    {
        var ct = TestContext.Current.CancellationToken;
        // Two very unevenly-sized runs: a naive mean of each run's own ratio (0.62625, 10)
        // would read 5.313125 - wildly inconsistent with avgCostPerRun/avgIssuesAccepted
        // in the same row. Total cost / total accepted (12.505 / 5) is the reconciling figure.
        await _repo.RecordRunAsync(
            Run("R6", "openai", "reviewer", "gpt-5", costUsd: 2.505m, raised: 5, accepted: 4),
            ct
        );
        await _repo.RecordRunAsync(
            Run("R7", "openai", "reviewer", "gpt-5", costUsd: 10.00m, raised: 1, accepted: 1),
            ct
        );

        var stats = await _repo.GetStatsAsync(ct);
        var openai = stats.Single(s => s.Reviewer == "openai");

        openai.AvgCostPerAcceptedFinding.Should().Be(12.505m / 5);
        openai.AvgCostPerRun.Should().Be(6.2525m);
        openai.AvgIssuesAccepted.Should().Be(2.5);
    }

    [Fact]
    public async Task Stats_exclude_unknown_cost_runs_from_cost_averages_and_count_them()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repo.RecordRunAsync(Run("U1", "zai", "reviewer", "glm", costUsd: 1m, accepted: 1), ct);
        await _repo.RecordRunAsync(Run("U2", "zai", "reviewer", "glm", costUsd: 3m, accepted: 1), ct);
        await _repo.RecordRunAsync(
            Run("U3", "zai", "reviewer", "glm", costUsd: 0m, accepted: 5, costUnknown: true),
            ct
        );

        var zai = (await _repo.GetStatsAsync(ct)).Single(s => s.Reviewer == "zai");

        zai.RunCount.Should().Be(3);
        zai.AvgCostPerRun.Should().Be(2m);
        zai.AvgCostPerAcceptedFinding.Should().Be(2m);
        zai.UnknownCostRuns.Should().Be(1);
    }

    [Fact]
    public async Task Stats_all_unknown_group_has_null_cost_averages()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repo.RecordRunAsync(Run("V1", "zai", "reviewer", "glm", costUsd: 0m, costUnknown: true), ct);
        await _repo.RecordRunAsync(Run("V2", "zai", "reviewer", "glm", costUsd: 0m, costUnknown: true), ct);

        var zai = (await _repo.GetStatsAsync(ct)).Single(s => s.Reviewer == "zai");

        zai.AvgCostPerRun.Should().BeNull();
        zai.AvgCostPerAcceptedFinding.Should().BeNull();
        zai.UnknownCostRuns.Should().Be(2);
    }

    [Fact]
    public async Task UpdateMetrics_overwrites_cost_unknown_on_existing_participant()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repo.RecordRunAsync(Run("W1", "zai", "reviewer", "glm", costUsd: 0m, costUnknown: true), ct);

        await _repo.RecordRunAsync(Run("W1", "zai", "reviewer", "glm", costUsd: 0.5m), ct);

        (await _repo.GetRunsAsync("W1", ct)).Should().ContainSingle().Which.CostUnknown.Should().BeFalse();
    }

    // A delayed placeholder re-post (unknown, cost 0) must not erase a measured cost; every other
    // ordering is last-write-wins.
    [Theory]
    [InlineData(false, 2.5, true, 0, false, 2.5)]
    [InlineData(true, 0, false, 1.5, false, 1.5)]
    [InlineData(false, 2.5, false, 1.5, false, 1.5)]
    [InlineData(true, 0, true, 0, true, 0)]
    public async Task UpdateMetrics_never_lets_an_unknown_cost_overwrite_a_known_one(
        bool firstUnknown,
        double firstCost,
        bool secondUnknown,
        double secondCost,
        bool expectedUnknown,
        double expectedCost
    )
    {
        var ct = TestContext.Current.CancellationToken;
        var runId = $"K-{firstUnknown}-{secondUnknown}";
        // The service sends a null per-finding cost for an unknown run; mirror that.
        await _repo.RecordRunAsync(
            Run(
                runId,
                "zai",
                "reviewer",
                "glm",
                (decimal)firstCost,
                costUnknown: firstUnknown,
                costPerAcceptedFinding: firstUnknown ? null : (decimal)firstCost / 2
            ),
            ct
        );
        await _repo.RecordRunAsync(
            Run(
                runId,
                "zai",
                "reviewer",
                "glm",
                (decimal)secondCost,
                raised: 7,
                costUnknown: secondUnknown,
                costPerAcceptedFinding: secondUnknown ? null : (decimal)secondCost / 2
            ),
            ct
        );

        var row = (await _repo.GetRunsAsync(runId, ct)).Should().ContainSingle().Which;
        row.CostUnknown.Should().Be(expectedUnknown);
        row.CostUsd.Should().Be((decimal)expectedCost);
        row.IssuesRaised.Should().Be(7, "non-cost columns still overwrite");
        if (expectedUnknown)
        {
            row.CostPerAcceptedFinding.Should().BeNull();
        }
        else
        {
            row.CostPerAcceptedFinding.Should().NotBeNull();
        }
    }

    [Theory]
    [InlineData(5, 0.50)]
    [InlineData(0, null)]
    public async Task UpdateMetrics_keeps_the_preserved_cost_with_its_model_and_tokens(
        int secondAccepted,
        double? expectedPerFinding
    )
    {
        var ct = TestContext.Current.CancellationToken;
        var runId = $"B-{secondAccepted}";
        var first = Run(
            runId,
            "zai",
            "reviewer",
            "model-a",
            2.5m,
            accepted: 2,
            costPerAcceptedFinding: 1.25m,
            tokens: 700
        );
        await _repo.RecordRunAsync(first, ct);
        var second = Run(
            runId,
            "zai",
            "reviewer",
            "model-b",
            0m,
            raised: 9,
            accepted: secondAccepted,
            costUnknown: true
        );
        await _repo.RecordRunAsync(second, ct);

        var row = (await _repo.GetRunsAsync(runId, ct)).Should().ContainSingle().Which;
        row.CostUsd.Should().Be(2.5m);
        row.CostUnknown.Should().BeFalse();
        row.Model.Should().Be("model-a");
        row.InputTokens.Should().Be(first.InputTokens);
        row.OutputTokens.Should().Be(first.OutputTokens);
        row.IssuesRaised.Should().Be(9);
        row.IssuesAccepted.Should().Be(secondAccepted);
        row.CostPerAcceptedFinding.Should().Be((decimal?)expectedPerFinding);

        var stats = (await _repo.GetStatsAsync(ct)).Where(s => s.Reviewer == "zai").ToList();
        stats.Should().ContainSingle().Which.Model.Should().Be("model-a");
    }

    [Fact]
    public async Task Stats_average_cost_per_finding_is_null_when_nothing_was_accepted()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repo.RecordRunAsync(
            Run("R8", "google", "reviewer", "gemini-2.5-pro", costUsd: 1m, raised: 3, accepted: 0),
            ct
        );

        var stats = await _repo.GetStatsAsync(ct);
        stats.Single(s => s.Reviewer == "google").AvgCostPerAcceptedFinding.Should().BeNull();
    }
}
