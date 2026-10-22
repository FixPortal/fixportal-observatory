using AiObservatory.Data;
using AiObservatory.Data.Audit;
using AiObservatory.Testing;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NodaTime.Testing;
using Npgsql;

namespace AiObservatory.Api.IntegrationTests;

public sealed class DatabaseTemplateTests
{
    [Fact]
    public async Task MigratedClonesAreIsolatedAndDuplicateCreationPreservesTheExistingDatabase()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = Connection();
        var second = Connection();
        await using var db1 = Context(first);
        await using var db2 = Context(second);
        try
        {
            await PostgresTestAssemblyFixture.CloneMigratedDatabaseAsync(first, ct);
            var migrationPasses = PostgresTestAssemblyFixture.TemplateMigrationPasses;
            await PostgresTestAssemblyFixture.CloneMigratedDatabaseAsync(second, ct);
            PostgresTestAssemblyFixture.TemplateMigrationPasses.Should().Be(migrationPasses);
            (await db1.Database.GetPendingMigrationsAsync(ct)).Should().BeEmpty();
            (await db2.Database.GetPendingMigrationsAsync(ct)).Should().BeEmpty();
            await new AuditStore(db1, new FakeClock(Instant.FromUtc(2026, 10, 22, 12, 0))).RegisterMachineAsync(
                new byte[32],
                ct
            );
            (await MachineCount(db2, ct)).Should().Be(0);
            var duplicate = () => PostgresTestAssemblyFixture.CloneMigratedDatabaseAsync(first, ct);
            await duplicate.Should().ThrowAsync<PostgresException>();
            (await MachineCount(db1, ct)).Should().Be(1);
        }
        finally
        {
            await db1.Database.EnsureDeletedAsync(CancellationToken.None);
            await db2.Database.EnsureDeletedAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task UnmigratedStartupStillAppliesTheRealMigrations()
    {
        await using var factory = new AiObservatoryApiFactory { UseMigratedTemplate = false };
        await factory.InitializeAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        (await db.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken)).Should().NotBeEmpty();
        (await db.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    private static string Connection() =>
        new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TEST_DB_CONNECTION"))
        {
            Database = $"aiobs_test_template_probe_{Guid.NewGuid():N}",
        }.ConnectionString;

    private static AiObservatoryDbContext Context(string connection) =>
        new(new DbContextOptionsBuilder<AiObservatoryDbContext>().UseNpgsql(connection, o => o.UseNodaTime()).Options);

    private static Task<int> MachineCount(AiObservatoryDbContext db, CancellationToken ct) =>
        db.Database.SqlQueryRaw<int>("""SELECT count(*)::integer AS "Value" FROM "AuditMachines" """).SingleAsync(ct);
}
