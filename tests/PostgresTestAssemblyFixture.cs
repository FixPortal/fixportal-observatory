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
    private string? _ownedConnection;
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
        _ownedConnection = _container.GetConnectionString();
        Environment.SetEnvironmentVariable("TEST_DB_CONNECTION", _ownedConnection);
    }

    public ValueTask DisposeAsync() =>
        DisposeOwnedAsync(
            Templates,
            _container,
            () =>
            {
                if (_ownedConnection is { } owned && Environment.GetEnvironmentVariable("TEST_DB_CONNECTION") == owned)
                {
                    Environment.SetEnvironmentVariable("TEST_DB_CONNECTION", null);
                }
            }
        );

    internal static async ValueTask DisposeOwnedAsync(
        ConcurrentDictionary<string, Lazy<OwnedTemplateDatabase>> templates,
        IAsyncDisposable? container,
        Action releaseConnection
    )
    {
        var failures = new List<Exception>();
        try
        {
            foreach (var entry in templates)
            {
                try
                {
                    if (entry.Value.IsValueCreated)
                    {
                        await entry.Value.Value.DisposeAsync();
                    }
                    templates.TryRemove(entry);
                }
                catch (Exception failure)
                {
                    // Retain this owner, including any initializer that outlived its bounded drain.
                    failures.Add(failure);
                }
            }
        }
        finally
        {
            if (container is not null)
            {
                try
                {
                    releaseConnection();
                }
                catch (Exception failure)
                {
                    failures.Add(failure);
                }
                try
                {
                    await container.DisposeAsync();
                }
                catch (Exception failure)
                {
                    failures.Add(failure);
                }
            }
        }
        if (failures.Count > 0)
        {
            throw new AggregateException("Owned PostgreSQL test cleanup failed.", failures);
        }
    }
}

/// <summary>Owns precisely one generated template and its started initialization.</summary>
internal sealed class OwnedTemplateDatabase : IAsyncDisposable
{
    private readonly string _connection;
    private readonly Func<string, CancellationToken, Task> _delete;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TimeSpan _drainTimeout;
    internal Task<string> Initialization { get; }

    internal OwnedTemplateDatabase(
        string adminConnection,
        Func<string, CancellationToken, Task> migrate,
        Func<string, CancellationToken, Task> delete,
        TimeSpan? drainTimeout = null
    )
    {
        _connection = new NpgsqlConnectionStringBuilder(adminConnection)
        {
            Database = $"aiobs_test_template_{Guid.NewGuid():N}",
            Pooling = false,
        }.ConnectionString;
        _delete = delete;
        _drainTimeout = drainTimeout ?? TimeSpan.FromSeconds(30);
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
            await Initialization.WaitAsync(_drainTimeout, CancellationToken.None);
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
