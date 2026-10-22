using System.Collections.Concurrent;
using AiObservatory.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(AiObservatory.Testing.PostgresTestAssemblyFixture))]

namespace AiObservatory.Testing;

/// <summary>
/// Supplies PostgreSQL automatically for Visual Studio and command-line test runs. CI or a
/// developer can still provide TEST_DB_CONNECTION explicitly; in that case no container is
/// created and the supplied database server remains entirely caller-owned.
/// </summary>
public sealed class PostgresTestAssemblyFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private static readonly ConcurrentDictionary<string, Lazy<Task<string>>> Templates = new(StringComparer.Ordinal);
    private static int _templateMigrationPasses;

    public static int TemplateMigrationPasses => Volatile.Read(ref _templateMigrationPasses);

    /// <summary>Copies only an assembly-owned, fully migrated empty database, never the supplied database.</summary>
    public static async Task CloneMigratedDatabaseAsync(string connectionString, CancellationToken ct)
    {
        var target = new NpgsqlConnectionStringBuilder(connectionString);
        var name = target.Database;
        if (
            name is null
            || !name.StartsWith("aiobs_test_", StringComparison.Ordinal)
            || name.Length < 33
            || !Guid.TryParseExact(name[^32..], "N", out _)
        )
        {
            throw new InvalidOperationException("Only uniquely named owned test databases may be cloned.");
        }
        var admin = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres", Pooling = false };
        var template = await Templates
            .GetOrAdd(
                admin.ConnectionString,
                _ => new Lazy<Task<string>>(() => MigrateTemplateAsync(admin.ConnectionString))
            )
            .Value.WaitAsync(ct);
        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync(ct);
        // Both identifiers are generated test names; quote them independently of connection-string credentials.
        using var identifiers = new NpgsqlCommandBuilder();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"CREATE DATABASE {identifiers.QuoteIdentifier(name)} TEMPLATE {identifiers.QuoteIdentifier(template)}";
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<string> MigrateTemplateAsync(string adminConnection)
    {
        var template = new NpgsqlConnectionStringBuilder(adminConnection)
        {
            Database = $"aiobs_test_template_{Guid.NewGuid():N}",
            Pooling = false,
        };
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(template.ConnectionString, o => o.UseNodaTime())
            .Options;
        await using var db = new AiObservatoryDbContext(options);
        try
        {
            await db.Database.MigrateAsync();
            Interlocked.Increment(ref _templateMigrationPasses);
            return template.Database;
        }
        catch
        {
            await db.Database.EnsureDeletedAsync();
            throw;
        }
        // Pooling=false ensures disposal closes every template connection before CREATE DATABASE TEMPLATE.
    }

    public async ValueTask InitializeAsync()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")))
        {
            return;
        }

        _container = new PostgreSqlBuilder("postgres:17").WithDatabase("postgres").Build();

        try
        {
            await _container.StartAsync();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Failed to start PostgreSQL for tests. Start Docker or set TEST_DB_CONNECTION.",
                ex
            );
        }
        Environment.SetEnvironmentVariable("TEST_DB_CONNECTION", _container.GetConnectionString());
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (adminConnection, pending) in Templates)
        {
            if (!pending.IsValueCreated || !pending.Value.IsCompletedSuccessfully)
            {
                continue;
            }
            var template = new NpgsqlConnectionStringBuilder(adminConnection) { Database = await pending.Value };
            var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
                .UseNpgsql(template.ConnectionString, o => o.UseNodaTime())
                .Options;
            await using var db = new AiObservatoryDbContext(options);
            await db.Database.EnsureDeletedAsync();
        }
        Templates.Clear();
        if (_container is null)
        {
            return;
        }

        Environment.SetEnvironmentVariable("TEST_DB_CONNECTION", null);
        await _container.DisposeAsync();
    }
}
