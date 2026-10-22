using System.Data.Common;
using AiObservatory.Api.Audit;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using AiObservatory.Data;
using AiObservatory.Data.Audit;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing;
using AiObservatory.Data.Pricing.Catalogs;
using AiObservatory.Data.Repositories;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using Npgsql;
using SourceKind = AiObservatory.Data.Entities.SourceKind;
using UsageScope = AiObservatory.Data.Entities.UsageScope;

namespace AiObservatory.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class AuditSnapshotServiceTests : IAsyncLifetime
{
    private static readonly Instant Now = Instant.FromUtc(2026, 10, 22, 12, 0);
    private readonly FakeClock _clock = new(Now);
    private readonly CancellationTokenSource _ceiling = CancellationTokenSource.CreateLinkedTokenSource(
        TestContext.Current.CancellationToken
    );
    private DbContextOptions<AiObservatoryDbContext> _options = null!;
    private AiObservatoryDbContext _db = null!;
    private AuditStore _store = null!;
    private CancellationToken Ct => _ceiling.Token;

    public async ValueTask InitializeAsync()
    {
        _ceiling.CancelAfter(TimeSpan.FromSeconds(45));
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TEST_DB_CONNECTION"))
        {
            Database = $"aiobs_test_snapshot_{Guid.NewGuid():N}",
        }.ConnectionString;
        await AiObservatory.Testing.PostgresTestAssemblyFixture.CloneMigratedDatabaseAsync(connection, Ct);
        _options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(connection, o => o.UseNodaTime())
            .Options;
        _db = new(_options);
        await _db.Database.MigrateAsync(Ct);
        _store = new(_db, _clock);
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
    public async Task Snapshot_IncludesAllEventsAndActualCanonicalInputDigest()
    {
        _db.UsageEvents.AddRange(
            Enumerable
                .Range(0, 10_003)
                .Select(i =>
                {
                    var e = Event(i);
                    if (i == 0)
                    {
                        e.InputTokens = 9_007_199_254_740_993;
                    }
                    return e;
                })
        );
        await _db.SaveChangesAsync(Ct);
        var lease = await Lease();
        var service = new AuditSnapshotService(_db, _store, lease);
        await service.CreateAsync(lease.RunId, Ct);
        var retained = await _store.ReadSnapshotAsync(lease.RunId, Ct);
        retained.Should().NotBeNull("snapshot creation must seal all server input");
        var input = AuditCanonicalJson.Deserialize<AuditInput>(retained.Bytes);
        input.Events.Should().HaveCount(10_003);
        input.Events.Should().Contain(e => e.Tokens.InputTokens == 9_007_199_254_740_993);
        input.Events.Should().OnlyContain(e => e.PricingApplication == null && e.SourceDimensions != null);
        retained.InputContentDigest.Should().Be(AuditComputationInput.ContentDigest(input));
        retained.Sha256.Should().Be(AuditCanonicalJson.ContentHash(retained.Bytes));
        System
            .Text.Encoding.UTF8.GetString(retained.Bytes)
            .Should()
            .NotContain("SECRET_CANARY")
            .And.NotContain("private-path");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Snapshot_ConcurrentCorrectionInsertRepriceAndRollback_RetainsOneRepeatableReadView(bool rollback)
    {
        var original = Event(1);
        var repository = new UsageRepository(_db);
        await repository.RecordEventAsync(original, Ct);
        var lease = await Lease();
        var gate = new SnapshotGate();
        await using var snapshotDb = new AiObservatoryDbContext(
            new DbContextOptionsBuilder<AiObservatoryDbContext>(_options).AddInterceptors(gate).Options
        );
        var freezing = new AuditSnapshotService(snapshotDb, new(snapshotDb, _clock), lease).CreateAsync(
            lease.RunId,
            Ct
        );
        await gate.Reached.Task.WaitAsync(Ct);
        try
        {
            await using var writer = new AiObservatoryDbContext(_options);
            var writes = new UsageRepository(writer);
            var correction = Event(1);
            correction.InputTokens = 9;
            correction.CostUsd = 1m;
            if (!rollback)
            {
                await writes.RecordEventAsync(correction, Ct);
                await writes.RecordEventAsync(Event(2), Ct);
            }
            var current = await writer.UsageEvents.AsNoTracking().FirstAsync(Ct);
            await using var tx = await writer.Database.BeginTransactionAsync(Ct);
            await writes.UpdateEventPricingAsync(current, new(3m, 0m), Ct);
            if (rollback)
            {
                await tx.RollbackAsync(Ct);
            }
            else
            {
                await tx.CommitAsync(Ct);
            }
        }
        finally
        {
            gate.Release.TrySetResult();
        }
        await freezing;
        var frozen = AuditCanonicalJson.Deserialize<AuditInput>(
            (await _store.ReadSnapshotAsync(lease.RunId, Ct))!.Bytes
        );
        frozen.Events.Should().ContainSingle().Which.Tokens.InputTokens.Should().Be(1);
        frozen.Events[0].CostUsd.Should().BeNull();
        frozen.Aggregates.Should().ContainSingle().Which.InputTokens.Should().Be(1);
        frozen.Aggregates[0].UnknownCostCount.Should().Be(1);
        (await _db.UsageEvents.CountAsync(Ct)).Should().Be(rollback ? 1 : 2);
    }

    [Fact]
    public async Task Snapshot_FreezesCompleteAggregateKeysCatalogFinancialFxAndSanitizedStatus()
    {
        var repository = new UsageRepository(_db);
        await repository.RecordEventAsync(Event(1), Ct);
        var second = Event(2);
        second.CostBasis = CostBasis.Notional;
        second.InputTokens = 20;
        second.CacheWrite1hTokens = 2;
        second.CacheWriteTokens = 4;
        await repository.RecordEventAsync(second, Ct);
        var catalog = new OpenAiPriceCatalog(
            "USD",
            "https://example.com/pricing",
            Now,
            [new("synthetic", ["synthetic"], new(2026, 10, 1), true, "standard", "short", "global", 2m, 0.5m, 10m, 3m)]
        );
        _db.PricingSnapshots.Add(
            new()
            {
                Provider = Provider.OpenAI,
                SourceId = "synthetic-pricing",
                ContentHash = new string('a', 64),
                RetrievedAt = Now,
                RawEvidence = "SECRET_CANARY",
                SourceUrl = "https://example.com/pricing",
                NormalizedCatalog = PricingCatalogJson.Serialize(catalog),
                IsActive = true,
            }
        );
        var status = await _db.SourceSyncStates.SingleAsync(s => s.SourceId == "synthetic", Ct);
        status.IsAvailable = false;
        status.LastError = "SECRET_CANARY";
        status.ConsecutiveFailureCount = 1;
        _db.SpendEntries.Add(
            new()
            {
                SourceId = "synthetic-spend",
                EntryKey = "C:/private-path",
                VendorId = await _db.SpendVendors.Select(v => v.Id).FirstAsync(Ct),
                CategoryId = await _db.SpendCategories.Select(c => c.Id).FirstAsync(Ct),
                OccurredOn = new(2026, 10, 22),
                Currency = "USD",
                Amount = 2m,
                AmountGbp = 1.5m,
                FxRate = 0.75m,
                RecordedAt = Now,
                ObservedAt = Now,
                RawPayload = "{\"secret\":\"SECRET_CANARY\"}",
            }
        );
        _db.BillingObservations.Add(
            new()
            {
                SourceId = "synthetic-billing",
                ProviderKey = "synthetic",
                ObservationKey = "secret-account",
                OccurredOn = new(2026, 10, 22),
                GrossAmount = 3m,
                CreditAmount = -1m,
                NetAmount = 2m,
                ObservedAt = Now,
                RawPayload = "{\"secret\":\"SECRET_CANARY\"}",
            }
        );
        await _db.SaveChangesAsync(Ct);
        var lease = await Lease();
        await new AuditSnapshotService(_db, _store, lease).CreateAsync(lease.RunId, Ct);
        var retained = (await _store.ReadSnapshotAsync(lease.RunId, Ct))!;
        var input = AuditCanonicalJson.Deserialize<AuditInput>(retained.Bytes);
        input.Aggregates.Should().HaveCount(2);
        input
            .Aggregates.Select(a => a.Key.CostBasis)
            .Should()
            .BeEquivalentTo([AccountingBasis.ListPriceEstimate, AccountingBasis.Notional]);
        input.Aggregates.Sum(a => a.InputTokens).Should().Be(21);
        input.Aggregates.Sum(a => a.CacheWrite1hTokens).Should().Be(2);
        input
            .PricingCatalogs.Should()
            .ContainSingle()
            .Which.Rates.Should()
            .Contain(r => r.Meter == PriceMeter.Input && r.UnitPrice == 2m);
        input
            .PricingCatalogs[0]
            .Rates.Should()
            .OnlyContain(r => r.CatalogEntryKey == PricingEvidence.RateKey(catalog.Entries[0]));
        input.Spend.Should().ContainSingle().Which.AmountGbp.Should().Be(1.5m);
        input.FxRates.Should().ContainSingle().Which.Rate.Should().Be(0.75m);
        input.FinancialObservations.Should().ContainSingle().Which.NetAmount.Should().Be(2m);
        input.SourceStatuses.Should().ContainSingle().Which.DiagnosticCode.Should().Be("source_error");
        System
            .Text.Encoding.UTF8.GetString(retained.Bytes)
            .Should()
            .NotContain("SECRET_CANARY")
            .And.NotContain("private-path")
            .And.NotContain("secret-account");
        await _db.PricingSnapshots.ExecuteUpdateAsync(set => set.SetProperty(p => p.IsActive, false), Ct);
        var retryId = await new AuditSnapshotService(_db, _store, lease).CreateAsync(lease.RunId, Ct);
        retryId.Should().Be(retained.SnapshotId);
        (await _store.ReadSnapshotAsync(lease.RunId, Ct))!.Bytes.Should().Equal(retained.Bytes);
    }

    [Fact]
    public async Task Snapshot_ExceededCompleteBudget_LeavesNoPartialSnapshot()
    {
        var lease = await Lease();
        var freeze = async () => await new AuditSnapshotService(_db, _store, lease, 100).CreateAsync(lease.RunId, Ct);
        (await freeze.Should().ThrowAsync<AuditStoreException>()).Which.Code.Should().Be("snapshot_size_limit");
        (await _store.ReadSnapshotAsync(lease.RunId, Ct)).Should().BeNull();
        await new AuditSnapshotService(_db, _store, lease).CreateAsync(lease.RunId, Ct);
        (await _store.ReadSnapshotAsync(lease.RunId, Ct)).Should().NotBeNull();
        var invalid = () => new AuditSnapshotService(_db, _store, lease, 512L * 1024 * 1024 + 1);
        invalid.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Snapshot_CancelDuringMaterialization_FencesSeal()
    {
        _db.UsageEvents.Add(Event(1));
        await _db.SaveChangesAsync(Ct);
        var lease = await Lease();
        var gate = new SnapshotGate();
        await using var snapshotDb = new AiObservatoryDbContext(
            new DbContextOptionsBuilder<AiObservatoryDbContext>(_options).AddInterceptors(gate).Options
        );
        var freeze = new AuditSnapshotService(snapshotDb, new(snapshotDb, _clock), lease).CreateAsync(lease.RunId, Ct);
        await gate.Reached.Task.WaitAsync(Ct);
        try
        {
            await _store.CancelRunAsync(lease.RunId, lease.Revision, Ct);
        }
        finally
        {
            gate.Release.TrySetResult();
        }
        var finish = async () => await freeze;
        (await finish.Should().ThrowAsync<AuditStoreException>()).Which.Code.Should().Be("snapshot_lease_lost");
        (await _store.ReadSnapshotAsync(lease.RunId, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Snapshot_RetainedCapture_FreezesOriginalTypedFactsWithServerInput()
    {
        var machine = await _store.RegisterMachineAsync(new byte[32], Ct);
        var scope = new AuditScope(
            new(2026, 10, 22),
            new(2026, 10, 22),
            [new(machine.MachineId, "claude-local")],
            [CheckCode.SourceAccounting],
            [],
            true
        );
        await _store.CreateCaptureRequestAsync(machine.MachineId, scope, Now, Ct);
        var captureLease = (await _store.TryClaimCaptureAsync(machine.MachineId, machine.CredentialId, Now, Ct))!;
        var record = new SourceUsageRecord
        {
            MachineId = machine.MachineId,
            SourceId = "claude-local",
            SourceFormat = "claude-jsonl",
            SourceVersion = "1",
            Tool = SourceTool.Claude,
            Kind = UsageRecordKind.SessionMetadata,
            Generation = "synthetic-generation",
            FileId = "opaque-file",
            Sequence = 1,
            Timestamp = Now.ToDateTimeOffset(),
            Pricing = new(ServiceTier.Unknown, false, null, null),
            Closure = new(ClosureState.Unknown, null, null),
        };
        byte[] chunk = [.. AuditCanonicalJson.Serialize(record), 10];
        var hash = AuditCanonicalJson.ContentHash(chunk);
        await _store.PutChunkAsync(captureLease, 0, chunk, hash, Ct);
        var manifest = new CaptureManifest(
            1,
            captureLease.RequestId,
            captureLease.RequestId,
            machine.MachineId,
            "synthetic-generation",
            "1",
            Now.ToDateTimeOffset(),
            Now.ToDateTimeOffset(),
            scope,
            [new("claude-local", "claude-jsonl", "1")],
            1,
            1,
            0,
            0,
            1,
            null,
            null,
            [],
            [hash],
            Now.ToDateTimeOffset()
        );
        var captureId = await _store.SealCaptureAsync(
            captureLease,
            AuditCanonicalJson.Serialize(manifest),
            "synthetic-seal",
            Ct
        );
        await _store.CreateRunAsync(scope, [captureId], "captured-run", false, Now, Ct);
        var lease = (await _store.TryLeaseRunAsync(Guid.NewGuid(), Now, Ct))!;
        await new AuditSnapshotService(_db, _store, lease).CreateAsync(lease.RunId, Ct);
        var retained = (await _store.ReadSnapshotAsync(lease.RunId, Ct))!;
        var input = AuditCanonicalJson.Deserialize<AuditInput>(retained.Bytes);
        input.Records.Should().ContainSingle().Which.Should().BeEquivalentTo(record);
        input.Captures.Should().ContainSingle().Which.ChunkHashes.Should().Equal(hash);
        retained.InputContentDigest.Should().Be(AuditComputationInput.ContentDigest(input));
    }

    [Fact]
    public async Task Snapshot_AnthropicNotionalDurationObservations_FreezeAnIndependentExactOrFallbackChoice()
    {
        var catalog = PricingCatalogJson.Serialize(
            new AnthropicPriceCatalog(
                "USD",
                "https://example.com/pricing",
                Now,
                [
                    new(
                        "synthetic",
                        ["synthetic"],
                        new(2026, 10, 1),
                        true,
                        2m,
                        10m,
                        0.2m,
                        2.5m,
                        4m,
                        null,
                        null,
                        null,
                        null,
                        null
                    ),
                ]
            )
        );
        _db.PricingSnapshots.Add(
            new()
            {
                Provider = Provider.Anthropic,
                SourceId = PricingSourceIds.Claude,
                ContentHash = new string('a', 64),
                RetrievedAt = Now,
                IsActive = true,
                NormalizedCatalog = catalog,
            }
        );
        await _db.SaveChangesAsync(Ct);
        var store = new PricingSnapshotStore(_db);
        var repository = new UsageRepository(
            _db,
            store,
            new UsagePriceResolver(store, [new AnthropicPriceCalculator()], NullLogger<UsagePriceResolver>.Instance)
        );
        string[] observations =
        [
            "{}",
            """{"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":100}}""",
            """{"cache_creation":{"ephemeral_5m_input_tokens":100,"ephemeral_1h_input_tokens":0}}""",
        ];
        foreach (var raw in observations)
        {
            await repository.RecordEstimatedEventAsync(
                new()
                {
                    Provider = Provider.Anthropic,
                    Model = "synthetic",
                    SourceId = "synthetic",
                    SourceKind = SourceKind.LocalTelemetry,
                    OccurredAt = Now,
                    ObservedAt = Now,
                    IngestedAt = Now,
                    CostBasis = CostBasis.Notional,
                    CacheWriteTokens = 100,
                    CacheWrite1hTokens = 100,
                    RawPayload = raw,
                },
                Ct
            );
        }
        var lease = await Lease();
        await new AuditSnapshotService(_db, _store, lease).CreateAsync(lease.RunId, Ct);
        var retained = (await _store.ReadSnapshotAsync(lease.RunId, Ct))!;
        var input = AuditCanonicalJson.Deserialize<AuditInput>(retained.Bytes);
        input.Events.Should().HaveCount(3);
        input
            .Events.Should()
            .OnlyContain(e =>
                e.CacheDurations != null && e.Tokens.CacheWrite1hTokens == 100 && e.PricingApplication != null
            );
        input.Events.Count(e => e.CacheDurations == new CacheDurationObservation(null, null)).Should().Be(1);
        input.Events.Count(e => e.CacheDurations == new CacheDurationObservation(100, 0)).Should().Be(1);
        input.Events.Count(e => e.CacheDurations == new CacheDurationObservation(0, 100)).Should().Be(1);
        foreach (var frozen in input.Events)
        {
            var observation = frozen.CacheDurations!;
            var writes = frozen.Tokens.CacheWriteTokens!.Value;
            var oneHour = frozen.Tokens.CacheWrite1hTokens!.Value;
            var exact = observation.FiveMinuteTokens == writes - oneHour && observation.OneHourTokens == oneHour;
            var effective1h = exact ? oneHour : 0;
            var effective5m = writes - effective1h;
            var rates = input
                .PricingCatalogs.Single()
                .Rates.Where(r => r.CatalogEntryKey == frozen.PricingApplication!.RuleId)
                .ToArray();
            var reference =
                effective5m / 1_000_000m * rates.Single(r => r.Meter == PriceMeter.CacheWrite5m).UnitPrice
                + effective1h / 1_000_000m * rates.Single(r => r.Meter == PriceMeter.CacheWrite1h).UnitPrice;
            frozen.CostUsd.Should().Be(reference);
        }
        retained.InputContentDigest.Should().Be(AuditComputationInput.ContentDigest(input));
    }

    private sealed class SnapshotGate : DbCommandInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _used;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default
        )
        {
            if (
                command.CommandText.Contains("LEFT JOIN \"PricingApplications\"", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _used, 1) == 0
            )
            {
                Reached.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    private async Task<RunLease> Lease()
    {
        var scope = new AuditScope(
            new(2026, 10, 22),
            new(2026, 10, 22),
            [new(Guid.NewGuid(), "synthetic")],
            [CheckCode.EventAggregation],
            [],
            true
        );
        await _store.CreateRunAsync(scope, [], Guid.NewGuid().ToString("N"), false, Now, Ct);
        return (await _store.TryLeaseRunAsync(Guid.NewGuid(), Now, Ct))!;
    }

    private static UsageEvent Event(int index) =>
        new()
        {
            Provider = Provider.OpenAI,
            Model = "synthetic",
            EventKey = $"event-{index}",
            SourceId = "synthetic",
            SourceKind = SourceKind.LocalTelemetry,
            UsageScope = UsageScope.Api,
            CostBasis = CostBasis.ListPriceEstimate,
            InputTokens = index,
            OccurredAt = Now,
            ObservedAt = Now,
            IngestedAt = Now,
            RawPayload =
                """{"processing":"standard","context":"short","region":"global","secret":"SECRET_CANARY","path":"C:/private-path"}""",
        };
}
