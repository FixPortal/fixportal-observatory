using System.Data.Common;
using AiObservatory.Data.Audit;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NodaTime;
using Npgsql;

namespace AiObservatory.Data.Tests.Audit;

[Trait("Category", "Integration")]
public sealed class AuditOperationalInventoryReaderTests : IAsyncLifetime
{
    private AiObservatoryDbContext _db = null!;
    private DbContextOptions<AiObservatoryDbContext> _options = null!;
    private readonly InventoryCommands _commands = new();
    private readonly CancellationTokenSource _ceiling = CancellationTokenSource.CreateLinkedTokenSource(
        TestContext.Current.CancellationToken
    );
    private CancellationToken Ct => _ceiling.Token;
    private static readonly Instant Past = Instant.FromUtc(2000, 1, 1, 0, 0);
    private static readonly Instant Future = Instant.FromUtc(2100, 1, 1, 0, 0);

    public async ValueTask InitializeAsync()
    {
        _ceiling.CancelAfter(TimeSpan.FromSeconds(40));
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TEST_DB_CONNECTION"))
        {
            Database = $"aiobs_test_inventory_{Guid.NewGuid():N}",
        }.ConnectionString;
        _options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(connection, o => o.UseNodaTime())
            .AddInterceptors(_commands)
            .Options;
        _db = new(_options);
        await Testing.PostgresTestAssemblyFixture.CloneMigratedDatabaseAsync(connection, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _db.Database.EnsureDeletedAsync(CancellationToken.None);
        }
        finally
        {
            await _db.DisposeAsync();
            _ceiling.Dispose();
        }
    }

    [Fact]
    public async Task EmptyReadDistinguishesAbsentPolicyWithoutInitializingOrTrackingRows()
    {
        var result = await Read();
        result.Policy.Should().Be(new InventoryPolicy(false, null, null, null));
        result.Runs.Total.Should().Be(0);
        result.ExportLeases.CapacityUnexpiredReservedBytes.Should().Be(0);
        result.Credentials.OldestCreatedAt.Should().BeNull();
        result.Credentials.NewestCreatedAt.Should().BeNull();
        result.MachineCount.Should().Be(0);
        _db.ChangeTracker.Entries().Should().BeEmpty();
        _db.ChangeTracker.QueryTrackingBehavior.Should().Be(QueryTrackingBehavior.TrackAll);
        (await PolicyCount()).Should().Be(0);
        _db.Database.CurrentTransaction.Should().BeNull();
    }

    [Theory]
    [InlineData(null, null, 0L)]
    [InlineData(90, 365, 7L)]
    public async Task StoredPolicyPreservesNullableDaysRevisionAndInstance(int? evidence, int? summary, long revision)
    {
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO public."AuditRetentionSettings" ("InstanceId","EvidenceDays","SummaryDays","Revision")
            VALUES ('default',{evidence},{summary},{revision}),('other',1,2,99)
            """,
            Ct
        );
        var result = await Read();
        result.Policy.Should().Be(new InventoryPolicy(true, evidence, summary, revision));
        (await PolicyCount()).Should().Be(2);
    }

    [Fact]
    public async Task RunsAndReservationsIncludeExpiredHistoryAndCancelledLiveCapacityOnlyForFixedInstance()
    {
        var first = await Run("default", "queued");
        await Run("default", "collecting");
        await Run("default", "executing", Future);
        await Run("default", "executing", Past);
        await Run("default", "executing");
        await Run("default", "completed", expired: true);
        await Run("default", "failed");
        await Run("default", "cancelled");
        var other = await Run("other", "queued");
        await Export(first, "default", false, Future, 11);
        await Export(first, "default", true, Future, long.MaxValue);
        await Export(first, "default", false, Past, 37);
        await Export(other, "other", false, Future, 999);
        var result = await Read();
        result.Runs.Should().Be(new InventoryRuns(8, 1, 1, 3, 1, 1, 1, 0, 1, 2, 1, 1));
        result.ExportLeases.Should().Be(new InventoryExportLeases(3, 1, 1, 1, 11, 2, (decimal)long.MaxValue + 11));
        (await new AuditOperationalInventoryReader(_db, "other").ReadAsync(Ct)).Runs.Total.Should().Be(1);
    }

    [Fact]
    public async Task GlobalMetadataPreservesOverlappingExpiryAndCredentialMeaning()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO public."AuditMachines" ("Id","CreatedAt") VALUES ({first},{Past}),({second},{Past})
            """,
            Ct
        );
        await Credential(first, Past, null);
        await Credential(first, Past + Duration.FromDays(1), Past);
        await Credential(second, Past + Duration.FromDays(2), null);
        var requests = new[]
        {
            await Request(first, "pending", Past),
            await Request(first, "uploading", Past, Future),
            await Request(first, "uploading", Future, Past),
            await Request(second, "uploading", Future),
            await Request(second, "sealed", Past),
            await Request(second, "expired", Past),
        };
        await Capture(requests[0], first, null, null);
        await Capture(requests[1], first, Future, null);
        await Capture(requests[2], first, Past, null);
        await Capture(requests[3], second, Past, Past);
        var result = await Read();
        result.CaptureRequests.Should().Be(new InventoryCaptureRequests(6, 1, 3, 1, 1, 0, 2, 1, 1, 1));
        result.Captures.Should().Be(new InventoryCaptures(4, 2, 1, 2));
        result.Credentials.Should().Be(new InventoryCredentials(3, 2, 1, 2, Past, Past + Duration.FromDays(2)));
        result.MachineCount.Should().Be(2);
        (await new AuditOperationalInventoryReader(_db, "other").ReadAsync(Ct))
            .CaptureRequests.Should()
            .Be(result.CaptureRequests);
    }

    [Fact]
    public async Task EightScalarQueriesShareReadonlyRepeatableReadAndBoundedSessionSettings()
    {
        _commands.InspectTransaction = true;
        var result = await Read("literal'instance");
        result.ObservedAtUtc.Should().Be(_commands.ObservedTimestamp);
        _commands.ReadOnly.Should().Be("on");
        _commands.Isolation.Should().Be("repeatable read");
        _commands.StatementTimeout.Should().Be("3s");
        _commands.LockTimeout.Should().Be("1s");
        _commands.IdleTimeout.Should().Be("5s");
        _commands.Selects.Should().HaveCount(8);
        string.Join('\n', _commands.Selects)
            .Should()
            .NotContain("SecretHash")
            .And.NotContain("Manifest")
            .And.NotContain("literal'instance");
        _commands.InstanceParameterCommands.Should().Be(3);
        _db.Database.CurrentTransaction.Should().BeNull();
    }

    [Fact]
    public async Task LaterSectionsRetainObservationSnapshotWhenAnotherConnectionCommits()
    {
        _commands.AfterObservation = async () =>
        {
            await using var writer = new AiObservatoryDbContext(_options);
            await writer.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO public."AuditMachines" ("Id","CreatedAt") VALUES ({Guid.NewGuid()},{Past})
                """,
                Ct
            );
        };
        var result = await Read();
        result.MachineCount.Should().Be(0);
        (
            await _db
                .Database.SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM public.\"AuditMachines\"")
                .SingleAsync(Ct)
        )
            .Should()
            .Be(1);
    }

    [Fact]
    public async Task DatabaseRejectsAccidentalWriteAndReaderDisposesFailedTransaction()
    {
        _commands.AttemptWrite = true;
        var action = () => Read();
        (await action.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should()
            .Be(PostgresErrorCodes.ReadOnlySqlTransaction);
        _db.Database.CurrentTransaction.Should().BeNull();
        _commands.AttemptWrite = false;
        (
            await _db
                .Database.SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM public.\"AuditMachines\"")
                .SingleAsync(Ct)
        )
            .Should()
            .Be(0);
        (await Read()).MachineCount.Should().Be(0);
    }

    [Fact]
    public async Task CancellationRollsBackAndAllowsNextReadWithoutInventingZeros()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        _commands.Cancel = cancel;
        var action = () => new AuditOperationalInventoryReader(_db, "default").ReadAsync(cancel.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();
        _db.Database.CurrentTransaction.Should().BeNull();
        _commands.Cancel = null;
        (await Read()).Policy.StoredRowPresent.Should().BeFalse();
    }

    private Task<AuditOperationalInventory> Read(string instance = "default")
    {
        _commands.Selects.Clear();
        return new AuditOperationalInventoryReader(_db, instance).ReadAsync(Ct);
    }

    private Task<long> PolicyCount() =>
        _db
            .Database.SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM public.\"AuditRetentionSettings\"")
            .SingleAsync(Ct);

    private async Task<Guid> Run(string instance, string state, Instant? lease = null, bool expired = false)
    {
        var id = Guid.NewGuid();
        Instant? expiry = expired ? Past : null;
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO public."AuditRuns" ("Id","InstanceId","Scope","IdempotencyKey","RequestHash","State","Revision","Attempts","CreatedAt","LeaseExpiresAt","EvidenceExpiresAt","SummaryExpiresAt")
            VALUES ({id},{instance},{Array.Empty<byte>()},{id.ToString("N")},{new string(
                'a',
                64
            )},{state},0,0,{Past},{lease},{expiry},{expiry})
            """,
            Ct
        );
        return id;
    }

    private Task<int> Export(Guid run, string instance, bool cancelled, Instant expires, long reserved) =>
        _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO public."AuditExportReadLeases" ("Id","RunId","InstanceId","Cancelled","ExpiresAt","ReservedBytes")
            VALUES ({Guid.NewGuid()},{run},{instance},{cancelled},{expires},{reserved})
            """,
            Ct
        );

    private Task<int> Credential(Guid machine, Instant created, Instant? revoked) =>
        _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO public."AuditMachineCredentials" ("Id","MachineId","CreatedAt","RevokedAt","SecretHash")
            VALUES ({Guid.NewGuid()},{machine},{created},{revoked},{new byte[32]})
            """,
            Ct
        );

    private async Task<Guid> Request(Guid machine, string state, Instant deadline, Instant? lease = null)
    {
        var id = Guid.NewGuid();
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO public."AuditCaptureRequests" ("Id","MachineId","Scope","State","CreatedAt","Deadline","LeaseExpiresAt","Revision","UploadedBytes","RecordCount")
            VALUES ({id},{machine},{Array.Empty<byte>()},{state},{Past},{deadline},{lease},0,0,0)
            """,
            Ct
        );
        return id;
    }

    private Task<int> Capture(Guid request, Guid machine, Instant? expires, Instant? deleted) =>
        _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO public."AuditEvidenceCaptures" ("Id","RequestId","MachineId","Generation","Manifest","ManifestHash","SealKey","SealedAt","ExpiresAt","DeletedAt","Revision")
            VALUES ({Guid.NewGuid()},{request},{machine},'synthetic',{Array.Empty<byte>()},{new string(
                'b',
                64
            )},{Guid.NewGuid().ToString("N")},{Past},{expires},{deleted},0)
            """,
            Ct
        );

    private sealed class InventoryCommands : DbCommandInterceptor
    {
        public List<string> Selects { get; } = [];
        public int InstanceParameterCommands { get; private set; }
        public bool InspectTransaction { get; set; }
        public bool AttemptWrite { get; set; }
        public CancellationTokenSource? Cancel { get; set; }
        public Func<Task>? AfterObservation { get; set; }
        public string? ReadOnly { get; private set; }
        public string? Isolation { get; private set; }
        public string? StatementTimeout { get; private set; }
        public string? LockTimeout { get; private set; }
        public string? IdleTimeout { get; private set; }
        public Instant ObservedTimestamp { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default
        )
        {
            Selects.Add(command.CommandText);
            if (
                AfterObservation != null
                && command.CommandText.Contains("FROM public.\"AuditRetentionSettings\"", StringComparison.Ordinal)
            )
            {
                var callback = AfterObservation;
                AfterObservation = null;
                await callback();
            }
            if (command.Parameters.Cast<DbParameter>().Any(p => Equals(p.Value, "literal'instance")))
            {
                InstanceParameterCommands++;
            }
            if (command.CommandText.Contains("current_timestamp AS \"Value\"", StringComparison.Ordinal))
            {
                if (InspectTransaction)
                {
                    ReadOnly = await Setting(command, "transaction_read_only", cancellationToken);
                    Isolation = await Setting(command, "transaction_isolation", cancellationToken);
                    StatementTimeout = await Setting(command, "statement_timeout", cancellationToken);
                    LockTimeout = await Setting(command, "lock_timeout", cancellationToken);
                    IdleTimeout = await Setting(command, "idle_in_transaction_session_timeout", cancellationToken);
                    await using var timestamp = command.Connection!.CreateCommand();
                    timestamp.Transaction = command.Transaction;
                    timestamp.CommandText = "SELECT current_timestamp";
                    ObservedTimestamp = (Instant)(await timestamp.ExecuteScalarAsync(cancellationToken))!;
                }
                if (AttemptWrite)
                {
                    await using var write = command.Connection!.CreateCommand();
                    write.Transaction = command.Transaction;
                    write.CommandText =
                        "INSERT INTO public.\"AuditMachines\" (\"Id\",\"CreatedAt\") VALUES (@id,current_timestamp)";
                    var id = write.CreateParameter();
                    id.ParameterName = "id";
                    id.Value = Guid.NewGuid();
                    write.Parameters.Add(id);
                    await write.ExecuteNonQueryAsync(cancellationToken);
                }
                if (Cancel != null)
                {
                    await Cancel.CancelAsync();
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            return result;
        }

        private static async Task<string?> Setting(DbCommand observed, string setting, CancellationToken ct)
        {
            await using var command = observed.Connection!.CreateCommand();
            command.Transaction = observed.Transaction;
            // Test-only constants, never caller input.
            command.CommandText = "SHOW " + setting;
            return (string?)await command.ExecuteScalarAsync(ct);
        }
    }
}
