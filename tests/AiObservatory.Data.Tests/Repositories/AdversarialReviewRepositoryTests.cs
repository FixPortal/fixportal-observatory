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
        Instant? recordedAt = null
    ) =>
        new()
        {
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
