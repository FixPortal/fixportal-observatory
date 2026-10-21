using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Npgsql;

namespace AiObservatory.Api.IntegrationTests;

/// <summary>
/// Startup backfill of the legacy BUDGET_ALERT_EMAIL_TO value into NotificationSettings. Each
/// case boots the real composition root, because the skip-when-a-row-exists guard lives in
/// Program's top-level startup code and the unique-violation swallow needs a real PostgreSQL
/// conflict: neither is reachable by calling the seed method directly.
/// </summary>
public sealed class LegacyAlertEmailStartupTests
{
    private const string LegacyEmail = "legacy@example.test";
    private const string OperatorEmail = "operator@example.test";

    [Fact]
    public async Task SeedsTheLegacyRecipientIntoAnEmptyDatabase()
    {
        await using var factory = new AiObservatoryApiFactory();
        factory.ConfigurationOverrides["BUDGET_ALERT_EMAIL_TO"] = LegacyEmail;

        var row = await ReadSettingsAsync(factory);

        row.Should().NotBeNull("the backfill must run for a deployment that predates the table");
        row.AlertEmailTo.Should().Be(LegacyEmail);
    }

    [Fact]
    public async Task KeepsAnOperatorConfiguredRecipientWhenALegacyValueIsStillSet()
    {
        var connectionString = DatabaseConnectionString();
        await using (var first = new AiObservatoryApiFactory())
        {
            // The first boot creates and migrates the database; its row is what the restart finds.
            first.SetDbConnection(connectionString);
            await SeedOperatorRowAsync(first);
        }

        await using var second = new AiObservatoryApiFactory();
        second.SetDbConnection(connectionString);
        second.ConfigurationOverrides["BUDGET_ALERT_EMAIL_TO"] = LegacyEmail;
        try
        {
            var row = await ReadSettingsAsync(second);

            row.Should().NotBeNull();
            row.AlertEmailTo.Should().Be(OperatorEmail, "a restart must not overwrite what the operator configured");
        }
        finally
        {
            await DeleteDatabaseAsync(connectionString);
        }
    }

    [Fact]
    public async Task SwallowsTheUniqueViolationWhenAnotherInstanceWinsTheInsertRace()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var ct = timeout.Token;
        var connectionString = DatabaseConnectionString();
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(connectionString, o => o.UseNodaTime())
            .Options;
        await using var holder = new AiObservatoryDbContext(options);
        try
        {
            await holder.Database.MigrateAsync(ct);
            await using var holderTx = await holder.Database.BeginTransactionAsync(ct);
            holder.NotificationSettings.Add(
                new NotificationSettings
                {
                    AlertEmailTo = OperatorEmail,
                    UpdatedAt = Instant.FromUtc(2026, 8, 4, 12, 0),
                }
            );
            await holder.SaveChangesAsync(ct);

            // The other instance has inserted but not committed, so this host's guard sees no row
            // and its own INSERT parks on the primary key until the holder commits.
            await using var factory = new AiObservatoryApiFactory();
            factory.SetDbConnection(connectionString);
            factory.ConfigurationOverrides["BUDGET_ALERT_EMAIL_TO"] = LegacyEmail;
            var startup = Task.Run(() => factory.Services, ct);
            await WaitForBlockedInsertAsync(connectionString, startup, ct);
            await holderTx.CommitAsync(ct);

            await startup;
            var row = await ReadSettingsAsync(factory);
            row.Should().NotBeNull();
            row.AlertEmailTo.Should().Be(OperatorEmail, "the winner's row stands and the loser starts cleanly");
        }
        finally
        {
            await holder.Database.EnsureDeletedAsync(CancellationToken.None);
        }
    }

    private static async Task<NotificationSettings?> ReadSettingsAsync(AiObservatoryApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        return await db
            .NotificationSettings.AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == NotificationSettings.SingletonId, TestContext.Current.CancellationToken);
    }

    private static async Task SeedOperatorRowAsync(AiObservatoryApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        db.NotificationSettings.Add(
            new NotificationSettings { AlertEmailTo = OperatorEmail, UpdatedAt = Instant.FromUtc(2026, 8, 4, 12, 0) }
        );
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task DeleteDatabaseAsync(string connectionString)
    {
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(connectionString, o => o.UseNodaTime())
            .Options;
        await using var db = new AiObservatoryDbContext(options);
        await db.Database.EnsureDeletedAsync(CancellationToken.None);
    }

    private static string DatabaseConnectionString()
    {
        var baseConnection =
            Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
            ?? "Host=localhost;Database=aiobs_test;Username=postgres;Password=postgres";
        return new NpgsqlConnectionStringBuilder(baseConnection)
        {
            Database = $"aiobs_test_legacy_email_{Guid.NewGuid():N}",
        }.ConnectionString;
    }

    /// <summary>
    /// Waits until another session is in a PostgreSQL lock wait on the NotificationSettings
    /// INSERT: the signal that the race has been forced, in place of a delay. It polls on its own
    /// connection because pg_stat_activity is snapshotted per transaction, so a poll inside the
    /// holder's open transaction would keep seeing the first picture.
    /// </summary>
    private static async Task WaitForBlockedInsertAsync(string connectionString, Task startup, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM pg_stat_activity
                WHERE datname = current_database()
                    AND pid <> pg_backend_pid()
                    AND wait_event_type = 'Lock'
                    AND query LIKE 'INSERT INTO "NotificationSettings"%'
            )
            """,
            connection
        );
        while (true)
        {
            if (startup.IsCompleted)
            {
                await startup;
                throw new InvalidOperationException("Host startup finished without parking on the insert.");
            }

            if ((bool)(await command.ExecuteScalarAsync(ct))!)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
        }
    }
}
