using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace AiObservatory.Data.Tests.Repositories;

[Trait("Category", "Integration")]
public class AdversarialReviewMigrationTests : IAsyncLifetime
{
    private DbContextOptions<AiObservatoryDbContext> _options = null!;

    public ValueTask InitializeAsync()
    {
        var baseConnection = Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
            ?? "Host=localhost;Database=aiobs_test;Username=postgres;Password=postgres";
        var connectionString = new NpgsqlConnectionStringBuilder(baseConnection)
        {
            Database = $"aiobs_test_migration_{Guid.NewGuid():N}",
        }.ConnectionString;
        _options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(connectionString, options => options.UseNodaTime())
            .Options;
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await using var db = new AiObservatoryDbContext(_options);
        await db.Database.EnsureDeletedAsync();
    }

    /// <summary>
    /// Every row of a run that already has finding rows is backfilled as captured; a run without
    /// finding rows stays counts-only, because nothing records whether it was posted as [].
    /// </summary>
    [Fact]
    public async Task AddAdversarialReviewFindingsCaptured_marks_only_runs_that_have_finding_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = new AiObservatoryDbContext(_options);
        var migrator = db.Database.GetService<IMigrator>();
        await migrator.MigrateAsync("20261121151305_BudgetAlertClaimInsightSetNull", ct);
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "AdversarialReviewRuns"
                ("Id", "Reviewer", "Model", "Role", "InputTokens", "OutputTokens", "CostUsd",
                 "ReviewDurationMs", "IssuesRaised", "IssuesAccepted", "RunId", "RecordedAt")
            VALUES
                (gen_random_uuid(), 'zai', 'glm', 'reviewer', 0, 0, 1, 0, 0, 0, 'DETAILED', now()),
                (gen_random_uuid(), 'anthropic', 'opus', 'judge', 0, 0, 1, 0, 0, 0, 'DETAILED', now()),
                (gen_random_uuid(), 'zai', 'glm', 'reviewer', 0, 0, 1, 0, 2, 1, 'COUNTS', now());
            INSERT INTO "AdversarialReviewFindings"
                ("Id", "RunId", "ChunkId", "FindingId", "Severity", "Outcome", "RecordedAt")
            VALUES (gen_random_uuid(), 'DETAILED', 'single', 'F1', 'low', 'accepted', now());
            """,
            ct
        );

        await migrator.MigrateAsync(cancellationToken: ct);

        var rows = await db.AdversarialReviewRuns.AsNoTracking().ToListAsync(ct);
        rows.Where(r => r.RunId == "DETAILED").Should().HaveCount(2).And.OnlyContain(r => r.FindingsCaptured);
        rows.Should().ContainSingle(r => r.RunId == "COUNTS").Which.FindingsCaptured.Should().BeFalse();
    }

    /// <summary>
    /// A clean run (captured, no finding rows) cannot be re-derived by the Up backfill, so dropping
    /// the column would silently turn it back into a counts-only run. The rollback refuses instead.
    /// </summary>
    [Fact]
    public async Task AddAdversarialReviewFindingsCaptured_rollback_refuses_while_a_clean_run_is_marked()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = new AiObservatoryDbContext(_options);
        var migrator = db.Database.GetService<IMigrator>();
        await migrator.MigrateAsync(cancellationToken: ct);
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "AdversarialReviewRuns"
                ("Id", "Reviewer", "Model", "Role", "InputTokens", "OutputTokens", "CostUsd",
                 "ReviewDurationMs", "IssuesRaised", "IssuesAccepted", "RunId", "RecordedAt", "FindingsCaptured")
            VALUES (gen_random_uuid(), 'zai', 'glm', 'reviewer', 0, 0, 1, 0, 0, 0, 'CLEAN', now(), TRUE);
            """,
            ct
        );

        var refused = () => migrator.MigrateAsync("20261121151305_BudgetAlertClaimInsightSetNull", ct);

        await refused.Should().ThrowAsync<PostgresException>().WithMessage("*rollback refused*");
        (await db.AdversarialReviewRuns.AsNoTracking().SingleAsync(ct)).FindingsCaptured.Should().BeTrue();

        // Once nothing would be lost the same rollback applies.
        await db.Database.ExecuteSqlRawAsync("""UPDATE "AdversarialReviewRuns" SET "FindingsCaptured" = FALSE""", ct);
        await migrator.MigrateAsync("20261121151305_BudgetAlertClaimInsightSetNull", ct);
        var applied = await db
            .Database.SqlQuery<string>(
                $"""SELECT "MigrationId" AS "Value" FROM "__EFMigrationsHistory" WHERE "MigrationId" LIKE '%AddAdversarialReviewFindingsCaptured'"""
            )
            .ToListAsync(ct);
        applied.Should().BeEmpty();
    }
}
