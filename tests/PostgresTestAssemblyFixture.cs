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
    private static readonly ConcurrentDictionary<string, Lazy<OwnedTemplateDatabase>> Templates = new(
        StringComparer.Ordinal
    );
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
                _ => new Lazy<OwnedTemplateDatabase>(() =>
                    new OwnedTemplateDatabase(admin.ConnectionString, MigrateTemplateAsync, DeleteTemplateAsync)
                )
            )
            .Value.Initialization.WaitAsync(ct);
        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync(ct);
        // Both identifiers are generated test names; quote them independently of connection-string credentials.
        using var identifiers = new NpgsqlCommandBuilder();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"CREATE DATABASE {identifiers.QuoteIdentifier(name)} TEMPLATE {identifiers.QuoteIdentifier(template)}";
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task MigrateTemplateAsync(string connection, CancellationToken ct)
    {
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(connection, o => o.UseNodaTime())
            .Options;
        await using var db = new AiObservatoryDbContext(options);
        await db.Database.MigrateAsync(ct);
        Interlocked.Increment(ref _templateMigrationPasses);
    }

    private static async Task DeleteTemplateAsync(string connection, CancellationToken ct)
    {
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(connection, o => o.UseNodaTime())
            .Options;
        await using var db = new AiObservatoryDbContext(options);
        await db.Database.EnsureDeletedAsync(ct);
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
        foreach (var pending in Templates.Values)
        {
            if (pending.IsValueCreated)
            {
                await pending.Value.DisposeAsync();
            }
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

/// <summary>Owns precisely one generated template and its started initialization.</summary>
internal sealed class OwnedTemplateDatabase : IAsyncDisposable
{
    private readonly string _connection;
    private readonly Func<string, CancellationToken, Task> _delete;
    private readonly CancellationTokenSource _lifetime = new();
    internal Task<string> Initialization { get; }

    internal OwnedTemplateDatabase(
        string adminConnection,
        Func<string, CancellationToken, Task> migrate,
        Func<string, CancellationToken, Task> delete
    )
    {
        _connection = new NpgsqlConnectionStringBuilder(adminConnection)
        {
            Database = $"aiobs_test_template_{Guid.NewGuid():N}",
            Pooling = false,
        }.ConnectionString;
        _delete = delete;
        Initialization = Initialize(migrate);
        // Even a provider that ignores cancellation and outlives the bounded drain remains
        // owned by this object; its eventual fault is observed and Initialize cleans it up.
        _ = Initialization.ContinueWith(
            t =>
            {
                _ = t.Exception;
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }

    private async Task<string> Initialize(Func<string, CancellationToken, Task> migrate)
    {
        try
        {
            await migrate(_connection, _lifetime.Token);
            _lifetime.Token.ThrowIfCancellationRequested();
            return new NpgsqlConnectionStringBuilder(_connection).Database!;
        }
        catch (Exception original)
        {
            try
            {
                await Cleanup();
            }
            catch (Exception cleanup)
            {
                throw new AggregateException(original, cleanup);
            }
            throw;
        }
    }

    private async Task Cleanup()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await _delete(_connection, budget.Token).WaitAsync(budget.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        try
        {
            await Initialization.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Initialize has finished exact-owned cleanup before propagating cancellation.
            _lifetime.Dispose();
            return;
        }
        // A timeout/fault propagates without discarding fixture ownership.
        await Cleanup();
        _lifetime.Dispose();
    }
}
