using System.Data.Common;
using System.Text.Json;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing;
using AiObservatory.Data.Pricing.Catalogs;
using AiObservatory.Data.Repositories;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;
using Npgsql;
using NpgsqlTypes;
using NSubstitute;

namespace AiObservatory.Data.Tests.Pricing;

[Trait("Category", "Integration")]
public sealed class PricingRepricingServiceTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(
        JsonSerializerDefaults.Web
    ).ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
    private AiObservatoryDbContext _db = null!;
    private string _connectionString = null!;
    private UsageRepository _repository = null!;
    private PricingSnapshotStore _store = null!;
    private PricingRepricingService _repricing = null!;
    private readonly ITestOutputHelper _output;

    public PricingRepricingServiceTests(ITestOutputHelper output) => _output = output;

    public async ValueTask InitializeAsync()
    {
        var baseConnection = Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
            ?? "Host=localhost;Database=aiobs_test;Username=postgres;Password=postgres";
        _connectionString = new NpgsqlConnectionStringBuilder(baseConnection)
        {
            Database = $"aiobs_test_repricing_{Guid.NewGuid():N}",
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(_connectionString, npgsql => npgsql.UseNodaTime())
            .Options;
        _db = new AiObservatoryDbContext(options);
        await _db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        _repository = new UsageRepository(_db);
        _store = new PricingSnapshotStore(_db);
        var resolver = new UsagePriceResolver(
            _store,
            [new OpenAiPriceCalculator()],
            NullLogger<UsagePriceResolver>.Instance
        );
        _repricing = new PricingRepricingService(_db, _repository, resolver, _store);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _db.Database.EnsureDeletedAsync();
        }
        finally
        {
            await _db.DisposeAsync();
        }
    }

    [Fact]
    public async Task RepricingUpdatesOnlyEligibleEstimatesAndRepairsAggregateCoverage()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.ActivateAsync(Candidate("old", 1m), ct);
        var eligible = Event("known", CostBasis.ListPriceEstimate, 9m, 3m);
        var notional = Event("notional", CostBasis.Notional, 8m, 2m);
        var unknownModel = Event("missing", CostBasis.ListPriceEstimate, 7m, 1m, "unknown-model");
        var billed = Event("billed", CostBasis.Billed, 6m, 1m);
        var providerEstimated = Event("provider", CostBasis.ProviderEstimated, 5m, 1m);
        var legacy = Event("legacy", CostBasis.Unknown, 4m, 1m);
        foreach (var usage in new[] { eligible, notional, unknownModel, billed, providerEstimated, legacy })
        {
            await _repository.RecordEventAsync(usage, ct);
        }

        await _store.ActivateAsync(Candidate("new", 2m), ct);
        await _repricing.RepriceProviderAsync(Provider.OpenAI, ct);

        var saved = await _db.UsageEvents.AsNoTracking().ToDictionaryAsync(row => row.EventKey!, ct);
        saved["known"].CostUsd.Should().Be(2m);
        saved["known"].CacheSavingsUsd.Should().Be(0m);
        saved["notional"].CostUsd.Should().Be(2m);
        // Unpriceable (unknown model): the last known figures are kept, never blanked.
        saved["missing"].CostUsd.Should().Be(7m);
        saved["missing"].CacheSavingsUsd.Should().Be(1m);
        saved["billed"].CostUsd.Should().Be(6m);
        saved["provider"].CostUsd.Should().Be(5m);
        saved["legacy"].CostUsd.Should().Be(4m);

        var aggregate = await _db
            .DailyAggregates.AsNoTracking()
            .SingleAsync(row => row.Model == "unknown-model" && row.CostBasis == CostBasis.ListPriceEstimate, ct);
        aggregate.CostUsd.Should().Be(7m);
        aggregate.UnknownCostCount.Should().Be(0);
        aggregate.CacheSavingsUsd.Should().Be(1m);
        aggregate.UnknownCacheSavingsCount.Should().Be(0);
    }

    [Fact]
    public async Task RepricingKeepsTheLastKnownCostWhenNoSnapshotCoversTheEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.ActivateAsync(Candidate("old", 1m), ct);
        // Occurs before the earliest catalog EffectiveFrom (2026-08-01), as does all history
        // older than the bundled catalogs' 2026-08-24 stamps: Covers finds nothing, the
        // resolver returns null, and the pass must leave the known price untouched.
        var historic = Event(
            "historic",
            CostBasis.ListPriceEstimate,
            5m,
            1m,
            occurredAt: Instant.FromUtc(2026, 7, 1, 0, 0)
        );
        await _repository.RecordEventAsync(historic, ct);

        await _store.ActivateAsync(Candidate("new", 2m), ct);
        await _repricing.RepriceProviderAsync(Provider.OpenAI, ct);

        var saved = await _db.UsageEvents.AsNoTracking().SingleAsync(ct);
        saved.CostUsd.Should().Be(5m);
        saved.CacheSavingsUsd.Should().Be(1m);
        saved.CostBasis.Should().Be(CostBasis.ListPriceEstimate);
        var aggregate = await _db.DailyAggregates.AsNoTracking().SingleAsync(ct);
        aggregate.CostUsd.Should().Be(5m);
        aggregate.UnknownCostCount.Should().Be(0);
        aggregate.CacheSavingsUsd.Should().Be(1m);
    }

    [Fact]
    public async Task RepricingKeepsAKnownCacheSavingsFigureWhenTheCostIsUnknownAndNoSnapshotCoversTheEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.ActivateAsync(Candidate("old", 1m), ct);
        var historic = Event(
            "savings-only",
            CostBasis.ListPriceEstimate,
            cost: null,
            savings: 1m,
            occurredAt: Instant.FromUtc(2026, 7, 1, 0, 0)
        );
        await _repository.RecordEventAsync(historic, ct);

        await _store.ActivateAsync(Candidate("new", 2m), ct);
        await _repricing.RepriceProviderAsync(Provider.OpenAI, ct);

        var saved = await _db.UsageEvents.AsNoTracking().SingleAsync(ct);
        saved.CostUsd.Should().BeNull();
        saved.CacheSavingsUsd.Should().Be(1m);
    }

    [Fact]
    public async Task RepricingSkipsAnEventCorrectedAfterItsQuoteWasCalculated()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.ActivateAsync(Candidate("old", 1m), ct);
        var original = Event("raced", CostBasis.ListPriceEstimate, 1m, 0m);
        await _repository.RecordEventAsync(original, ct);

        // What the unlocked repricing scan read, and the quote it calculated from that read.
        var scanned = await _db.UsageEvents.AsNoTracking().SingleAsync(row => row.EventKey == "raced", ct);
        await _store.ActivateAsync(Candidate("new", 2m), ct);
        var quote = await Resolver(_store).ResolveAsync(scanned, ct);
        quote!.CostUsd.Should().Be(2m);

        // A concurrent ingest correction lands in the gap, doubling the tokens and pricing them itself.
        var corrected = Event("raced", CostBasis.ListPriceEstimate, 4m, 0m);
        corrected.InputTokens = 2_000_000;
        corrected.ObservedAt = original.ObservedAt.Plus(Duration.FromMinutes(1));
        await _repository.RecordEventAsync(corrected, ct);

        await _repository.UpdateEventPricingAsync(scanned, quote, ct);

        var saved = await _db.UsageEvents.AsNoTracking().SingleAsync(row => row.EventKey == "raced", ct);
        saved.InputTokens.Should().Be(2_000_000);
        saved.CostUsd.Should().Be(4m);
        var aggregate = await _db.DailyAggregates.AsNoTracking().SingleAsync(ct);
        aggregate.InputTokens.Should().Be(2_000_000);
        aggregate.CostUsd.Should().Be(4m);
    }

    [Fact]
    public async Task StandaloneRepricingCommitsEachEventInItsOwnTransaction()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.ActivateAsync(Candidate("old", 1m), ct);
        // Explicit ids pin the scan order (OrderBy Id), so the good event is repriced before
        // the calculator throws for the boom one. A pass-level transaction would roll the good
        // event's write back with the failure; per-event transactions must not.
        await _repository.RecordEventAsync(
            Event(
                "committed-first",
                CostBasis.ListPriceEstimate,
                1m,
                0m,
                id: new Guid("00000000-0000-0000-0000-000000000001")
            ),
            ct
        );
        await _repository.RecordEventAsync(
            Event(
                "boom",
                CostBasis.ListPriceEstimate,
                1m,
                0m,
                model: "boom-model",
                id: new Guid("00000000-0000-0000-0000-000000000002")
            ),
            ct
        );
        await _store.ActivateAsync(Candidate("new", 2m), ct);
        var sabotaged = new PricingRepricingService(
            _db,
            _repository,
            new UsagePriceResolver(
                _store,
                [new ThrowingOnModelCalculator("boom-model")],
                NullLogger<UsagePriceResolver>.Instance
            ),
            _store
        );

        var pass = async () => await sabotaged.RepriceProviderAsync(Provider.OpenAI, ct);

        await pass.Should().ThrowAsync<InvalidOperationException>();
        var costs = await _db
            .UsageEvents.AsNoTracking()
            .ToDictionaryAsync(row => row.EventKey!, row => row.CostUsd, ct);
        costs["committed-first"]
            .Should()
            .Be(2m, "each event commits in its own transaction, so a later failure cannot roll it back");
        costs["boom"].Should().Be(1m);
    }

    [Fact]
    public async Task StandaloneRepricingDoesNotHoldTheActivationLockAcrossThePass()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.ActivateAsync(Candidate("old", 1m), ct);
        await _repository.RecordEventAsync(Event("stalled", CostBasis.ListPriceEstimate, 1m, 0m), ct);
        await _store.ActivateAsync(Candidate("new", 2m), ct);
        var eventId = (await _db.UsageEvents.AsNoTracking().SingleAsync(ct)).Id;

        // Hold the event row so the pass blocks at its per-event write — after the snapshot
        // read that the shared activation lock protects.
        await using var gateConnection = new NpgsqlConnection(_connectionString);
        await gateConnection.OpenAsync(ct);
        await using var gateTransaction = await gateConnection.BeginTransactionAsync(ct);
        await using (var gateCommand = gateConnection.CreateCommand())
        {
            gateCommand.Transaction = gateTransaction;
            gateCommand.CommandText = """SELECT * FROM "UsageEvents" WHERE "Id" = @id FOR UPDATE""";
            gateCommand.Parameters.AddWithValue("id", eventId);
            await gateCommand.ExecuteNonQueryAsync(ct);
        }

        var pass = _repricing.RepriceProviderAsync(Provider.OpenAI, ct);
        try
        {
            // The pass's FOR UPDATE row load waits on the gate's xact (XactLockTableWait),
            // surfacing as an ungranted transactionid lock — the tuple locktag is released
            // before the xact wait begins.
            await WaitForBlockedLockAsync("transactionid", ct, gateConnection.ProcessID);
            pass.IsCompleted.Should().BeFalse();

            // A catalog activation takes the exclusive form of the same advisory lock; it can be
            // granted while the pass is still writing events only if the pass released its shared
            // lock with the short read transaction instead of holding it for the whole sweep.
            await using var probeConnection = new NpgsqlConnection(_connectionString);
            await probeConnection.OpenAsync(ct);
            await using var probeCommand = probeConnection.CreateCommand();
            probeCommand.CommandText = "SELECT pg_try_advisory_xact_lock(hashtextextended('openai-pricing', 0))";
            var acquired = (bool)(await probeCommand.ExecuteScalarAsync(ct))!;
            acquired
                .Should()
                .BeTrue("the standalone pass must not hold the shared activation lock across the whole sweep");
        }
        finally
        {
            // Release the gate even when the wait above timed out, so the pass finishes instead
            // of keeping _db busy through teardown.
            await gateTransaction.RollbackAsync(CancellationToken.None);
            await pass;
        }

        (await _db.UsageEvents.AsNoTracking().SingleAsync(ct)).CostUsd.Should().Be(2m);
    }

    [Fact]
    public async Task WaitForBlockedLockOnlyCountsWaitersBlockedByTheNamedSession()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.ActivateAsync(Candidate("old", 1m), ct);
        await _repository.RecordEventAsync(Event("contended", CostBasis.ListPriceEstimate, 1m, 0m), ct);
        var eventId = (await _db.UsageEvents.AsNoTracking().SingleAsync(ct)).Id;

        // The holder parks on the row; a second session queues behind it (a transactionid wait).
        await using var holder = new NpgsqlConnection(_connectionString);
        await holder.OpenAsync(ct);
        await using var holderTransaction = await holder.BeginTransactionAsync(ct);
        await using (var hold = holder.CreateCommand())
        {
            hold.Transaction = holderTransaction;
            hold.CommandText = """SELECT 1 FROM "UsageEvents" WHERE "Id" = @id FOR UPDATE""";
            hold.Parameters.AddWithValue("id", eventId);
            await hold.ExecuteNonQueryAsync(ct);
        }

        // An idle session that blocks nobody: a waiter it does not block must not satisfy the wait.
        await using var bystander = new NpgsqlConnection(_connectionString);
        await bystander.OpenAsync(ct);

        await using var waiter = new NpgsqlConnection(_connectionString);
        await waiter.OpenAsync(ct);
        await using var waiterCommand = waiter.CreateCommand();
        waiterCommand.CommandText = """UPDATE "UsageEvents" SET "Model" = "Model" WHERE "Id" = @id""";
        waiterCommand.Parameters.AddWithValue("id", eventId);
        var blockedUpdate = waiterCommand.ExecuteNonQueryAsync(ct);
        try
        {
            await WaitForBlockedLockAsync("transactionid", ct, holder.ProcessID);

            using var shortWait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            shortWait.CancelAfter(TimeSpan.FromSeconds(2));
            var act = () => WaitForBlockedLockAsync("transactionid", shortWait.Token, bystander.ProcessID);
            await act.Should().ThrowAsync<OperationCanceledException>();
        }
        finally
        {
            await holderTransaction.RollbackAsync(CancellationToken.None);
            await blockedUpdate;
        }
    }

    [Fact]
    public async Task StandaloneRepricingCachesANotionalsSnapshotsUnderTheActivationLock()
    {
        // A notional event's source was previously skipped by the under-lock snapshot cache on
        // the claim that GetCoveringSnapshotsAsync never serves notional resolutions from the
        // cache. It does — so the skip let a notional event trigger a live snapshot read inside
        // RepriceLockedAsync, AFTER the read transaction committed and the shared activation
        // lock was released, where a concurrent activation could price part of the pass from a
        // newer catalog generation. The probe fails the test if ANY PricingSnapshots read for
        // the pass runs outside the short locked read transaction.
        var ct = TestContext.Current.CancellationToken;
        await _store.ActivateAsync(Candidate("old", 1m), ct);
        await _repository.RecordEventAsync(Event("notional", CostBasis.Notional, 8m, 2m), ct);
        await _store.ActivateAsync(Candidate("new", 2m), ct);

        var probe = new SnapshotReadProbe();
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(_connectionString, npgsql => npgsql.UseNodaTime())
            .AddInterceptors(probe)
            .Options;
        await using var passDb = new AiObservatoryDbContext(options);
        var passStore = new PricingSnapshotStore(passDb);
        var passRepricing = new PricingRepricingService(
            passDb,
            new UsageRepository(passDb),
            Resolver(passStore),
            passStore
        );

        await passRepricing.RepriceProviderAsync(Provider.OpenAI, ct);

        probe
            .ReadsOutsideTransaction.Should()
            .Be(0, "the pre-cached notional snapshots must price the pass — no post-lock lazy read");
        probe
            .ReadsInsideTransaction.Should()
            .BeGreaterThan(0, "the notional event's source must be cached under the shared lock");
        (await _db.UsageEvents.AsNoTracking().SingleAsync(row => row.EventKey == "notional", ct))
            .CostUsd.Should()
            .Be(2m);
    }

    private sealed class SnapshotReadProbe : DbCommandInterceptor
    {
        public int ReadsInsideTransaction { get; private set; }
        public int ReadsOutsideTransaction { get; private set; }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default
        )
        {
            if (command.CommandText.Contains("PricingSnapshots", StringComparison.Ordinal))
            {
                if (eventData.Context?.Database.CurrentTransaction is not null)
                {
                    ReadsInsideTransaction++;
                }
                else
                {
                    ReadsOutsideTransaction++;
                }
            }

            return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task StandalonePassSendsOnlyEventsWhoseApplicationIsMissingOrChangedToTheRepository()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.ActivateAsync(Candidate("catalog", 2m), ct);
        var pricing = new UsageRepository(_db, _store, Resolver(_store));
        // Current: priced through the resolver, so it carries a stored application at its revision.
        var current = Event("current", CostBasis.ListPriceEstimate, null, null);
        (await pricing.RecordEstimatedEventAsync(current, ct)).Disposition.Should().Be(RecordEventDisposition.Created);
        // Legacy: the same price but no application row (revision 0), as a pre-migration event.
        var legacy = Event("legacy", CostBasis.ListPriceEstimate, 2m, 0m);
        await _repository.RecordEventAsync(legacy, ct);
        var repository = Substitute.For<IUsageRepository>();
        var repricing = new PricingRepricingService(_db, repository, Resolver(_store), _store);

        await repricing.RepriceProviderAsync(Provider.OpenAI, ct);

        await repository
            .Received(1)
            .UpdateEventPricingWithEvidenceAsync(
                Arg.Is<UsageEvent>(usage => usage.EventKey == "legacy"),
                Arg.Any<ResolvedPriceApplication?>(),
                Arg.Any<CancellationToken>()
            );
        await repository
            .DidNotReceive()
            .UpdateEventPricingWithEvidenceAsync(
                Arg.Is<UsageEvent>(usage => usage.EventKey == "current"),
                Arg.Any<ResolvedPriceApplication?>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task StandaloneRepricingAggregatesTheKeepLastKnownWarningPerModel()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.ActivateAsync(Candidate("catalog", 1m), ct);
        // ghost-a/ghost-b are in no retained catalog, so every event keeps its last known price.
        await _repository.RecordEventAsync(Event("ghost-1", CostBasis.ListPriceEstimate, 3m, 1m, "ghost-a"), ct);
        await _repository.RecordEventAsync(Event("ghost-2", CostBasis.ListPriceEstimate, 4m, 1m, "ghost-a"), ct);
        await _repository.RecordEventAsync(Event("ghost-3", CostBasis.ListPriceEstimate, 5m, 1m, "ghost-b"), ct);
        var logger = new CapturingLogger();
        var repricing = new PricingRepricingService(_db, _repository, Resolver(_store), _store, logger);

        await repricing.RepriceProviderAsync(Provider.OpenAI, ct);

        logger.Warnings.Should().HaveCount(2, "one line per (provider, model), not one per event");
        logger.Warnings.Should().Contain(message => message.Contains("skipped 2 event(s) for OpenAI/ghost-a"));
        logger.Warnings.Should().Contain(message => message.Contains("skipped 1 event(s) for OpenAI/ghost-b"));
    }

    [Fact]
    public async Task StandaloneRepricingBoundsTheLoggedModelLength()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.ActivateAsync(Candidate("catalog", 1m), ct);
        var longModel = "ghost-" + new string('x', 500);
        await _repository.RecordEventAsync(Event("ghost-long", CostBasis.ListPriceEstimate, 3m, 1m, longModel), ct);
        var logger = new CapturingLogger();
        var repricing = new PricingRepricingService(_db, _repository, Resolver(_store), _store, logger);

        await repricing.RepriceProviderAsync(Provider.OpenAI, ct);

        var warning = logger.Warnings.Should().ContainSingle().Which;
        warning.Should().NotContain(longModel);
        warning.Length.Should().BeLessThan(400);
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Performance")]
    public async Task QualificationRepricesEveryEligibleEventAndItsAggregate()
    {
        const int eventCount = 1_000;
        var ct = TestContext.Current.CancellationToken;
        await _store.ActivateAsync(Candidate("qualification-old", 1m), ct);
        _db.UsageEvents.AddRange(
            Enumerable
                .Range(0, eventCount)
                .Select(index => Event($"qualification-{index}", CostBasis.ListPriceEstimate, 1m, 0m))
        );
        _db.DailyAggregates.Add(
            new DailyAggregate
            {
                Date = new LocalDate(2026, 8, 25),
                Provider = Provider.OpenAI,
                Model = "gpt-test",
                SourceId = "repricing-test",
                SourceKind = SourceKind.ProviderApi,
                UsageScope = UsageScope.Api,
                CostBasis = CostBasis.ListPriceEstimate,
                InputTokens = eventCount * 1_000_000L,
                CostUsd = eventCount,
                CacheSavingsUsd = 0m,
                RequestCount = eventCount,
            }
        );
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();

        await _store.ActivateAsync(Candidate("qualification-warmup", 2m), ct, (_, callbackCt) =>
            _repricing.RepriceProviderAsync(Provider.OpenAI, callbackCt)
        );

        var elapsed = new List<TimeSpan>();
        foreach (var (price, iteration) in new[] { 3m, 4m, 5m }.Select((price, index) => (price, index)))
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var activation = await _store.ActivateAsync(
                Candidate($"qualification-{iteration}", price),
                ct,
                (_, callbackCt) => _repricing.RepriceProviderAsync(Provider.OpenAI, callbackCt)
            );
            stopwatch.Stop();
            activation.Should().Be(PricingActivationResult.Activated);
            elapsed.Add(stopwatch.Elapsed);
        }

        var median = elapsed.Order().ElementAt(elapsed.Count / 2);
        _output.WriteLine(
            $"events={eventCount}; measuredIterations={elapsed.Count}; medianMs={median.TotalMilliseconds:F1}; "
                + $"eventsPerSecond={eventCount / median.TotalSeconds:F1}; "
                + $"runsMs=[{string.Join(", ", elapsed.Select(run => run.TotalMilliseconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)))}]"
        );

        var repricedCount = await _db.UsageEvents.AsNoTracking().CountAsync(usage => usage.CostUsd == 5m, ct);
        repricedCount.Should().Be(eventCount);
        var aggregate = await _db.DailyAggregates.AsNoTracking().SingleAsync(ct);
        aggregate.CostUsd.Should().Be(eventCount * 5m);
        aggregate.RequestCount.Should().Be(eventCount);
    }

    [Fact]
    public async Task ActivationCallbackCommitsEffectiveDateRepricingWithTheSnapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.ActivateAsync(Candidate("old", 1m), ct);
        await _repository.RecordEventAsync(Event("before", CostBasis.ListPriceEstimate, 1m, 0m), ct);
        await _repository.RecordEventAsync(
            Event("after", CostBasis.ListPriceEstimate, 1m, 0m, occurredAt: Instant.FromUtc(2026, 9, 2, 0, 0)),
            ct
        );

        await _store.ActivateAsync(Candidate("windowed", 2m, (new LocalDate(2026, 9, 1), 3m)), ct, (_, callbackCt) =>
            _repricing.RepriceProviderAsync(Provider.OpenAI, callbackCt)
        );

        var costs = await _db
            .UsageEvents.AsNoTracking()
            .OrderBy(row => row.EventKey)
            .Select(row => row.CostUsd)
            .ToListAsync(ct);
        costs.Should().Equal(3m, 2m);
        (await _store.GetActiveAsync(PricingSourceIds.OpenAi, ct))!.RawEvidence.Should().Be("windowed");
    }

    [Fact]
    public async Task EstimatedInsertPausedAcrossActivationCannotCommitTheOldPriceAfterActivation()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.ActivateAsync(Candidate("old", 1m), ct);

        await using var gateConnection = new NpgsqlConnection(_connectionString);
        await gateConnection.OpenAsync(ct);
        await using var gateTransaction = await gateConnection.BeginTransactionAsync(ct);
        await using (var gateCommand = gateConnection.CreateCommand())
        {
            gateCommand.Transaction = gateTransaction;
            gateCommand.CommandText = "LOCK TABLE \"DailyAggregates\" IN ACCESS EXCLUSIVE MODE";
            await gateCommand.ExecuteNonQueryAsync(ct);
        }

        await using var writeDb = CreateContext();
        var writeStore = new PricingSnapshotStore(writeDb);
        var writeResolver = Resolver(writeStore);
        var writer = new UsageRepository(writeDb, writeStore, writeResolver);
        var record = writer.RecordEstimatedEventAsync(Event("overlap", CostBasis.ListPriceEstimate, 99m, 99m), ct);
        await WaitForBlockedLockAsync("relation", ct, gateConnection.ProcessID);
        record.IsCompleted.Should().BeFalse();

        await using var activationDb = CreateContext();
        var activationStore = new PricingSnapshotStore(activationDb);
        var activationResolver = Resolver(activationStore);
        var activationRepository = new UsageRepository(activationDb);
        var activationRepricing = new PricingRepricingService(
            activationDb,
            activationRepository,
            activationResolver,
            activationStore
        );
        var activation = activationStore.ActivateAsync(Candidate("new", 2m), ct, (_, callbackCt) =>
            activationRepricing.RepriceProviderAsync(Provider.OpenAI, callbackCt)
        );
        await WaitForBlockedLockAsync("advisory", ct);
        activation.IsCompleted.Should().BeFalse();

        await gateTransaction.RollbackAsync(ct);
        (await record).Disposition.Should().Be(RecordEventDisposition.Created);
        (await activation).Should().Be(PricingActivationResult.Activated);

        await using var verificationDb = CreateContext();
        var saved = await verificationDb.UsageEvents.AsNoTracking().SingleAsync(row => row.EventKey == "overlap", ct);
        saved.CostUsd.Should().Be(2m);
        saved.CacheSavingsUsd.Should().Be(0m);
        (await verificationDb.DailyAggregates.AsNoTracking().SingleAsync(ct)).CostUsd.Should().Be(2m);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActivationCallbackFailureRollsBackSnapshotEventsAndAggregates(bool cancel)
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.ActivateAsync(Candidate("old", 1m), ct);
        await _repository.RecordEventAsync(Event("event", CostBasis.ListPriceEstimate, 1m, 0m), ct);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var activationToken = cancel ? cancellation.Token : ct;

        var activation = async () =>
            await _store.ActivateAsync(Candidate("new", 2m), activationToken, async (_, callbackCt) =>
            {
                await _repricing.RepriceProviderAsync(Provider.OpenAI, callbackCt);
                if (cancel)
                {
                    await cancellation.CancelAsync();
                    callbackCt.ThrowIfCancellationRequested();
                }

                throw new InvalidOperationException("fail activation");
            });

        if (cancel)
        {
            await activation.Should().ThrowAsync<OperationCanceledException>();
        }
        else
        {
            await activation.Should().ThrowAsync<InvalidOperationException>();
        }

        (await _store.GetActiveAsync(PricingSourceIds.OpenAi, ct))!.RawEvidence.Should().Be("old");
        (await _db.UsageEvents.AsNoTracking().SingleAsync(ct)).CostUsd.Should().Be(1m);
        (await _db.DailyAggregates.AsNoTracking().SingleAsync(ct)).CostUsd.Should().Be(1m);
    }

    private static UsageEvent Event(
        string key,
        CostBasis basis,
        decimal? cost,
        decimal? savings,
        string model = "gpt-test",
        Instant? occurredAt = null,
        Guid? id = null
    ) =>
        new()
        {
            Id = id ?? Guid.NewGuid(),
            Provider = Provider.OpenAI,
            OccurredAt = occurredAt ?? Instant.FromUtc(2026, 8, 25, 0, 0),
            IngestedAt = Instant.FromUtc(2026, 8, 25, 1, 0),
            ObservedAt = Instant.FromUtc(2026, 8, 25, 1, 0),
            Model = model,
            InputTokens = 1_000_000,
            CostUsd = cost,
            CacheSavingsUsd = savings,
            CostBasis = basis,
            SourceId = "repricing-test",
            SourceKind = SourceKind.ProviderApi,
            UsageScope = UsageScope.Api,
            EventKey = key,
            RawPayload = """{"processing":"standard","context":"short","region":"global"}""",
        };

    private AiObservatoryDbContext CreateContext() =>
        new(
            new DbContextOptionsBuilder<AiObservatoryDbContext>()
                .UseNpgsql(_connectionString, npgsql => npgsql.UseNodaTime())
                .Options
        );

    private static UsagePriceResolver Resolver(PricingSnapshotStore store) =>
        new(store, [new OpenAiPriceCalculator()], NullLogger<UsagePriceResolver>.Instance);

    private async Task WaitForBlockedLockAsync(
        string lockType,
        CancellationToken cancellationToken,
        int? blockerPid = null
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(timeout.Token);
        while (true)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT count(*)
                FROM pg_locks
                WHERE locktype = @lockType
                  AND NOT granted
                  AND (database IS NULL OR database = (SELECT oid FROM pg_database WHERE datname = current_database()))
                  AND (@lockType <> 'relation' OR relation = '"DailyAggregates"'::regclass)
                  AND (@blockerPid::int IS NULL OR @blockerPid::int = ANY(pg_blocking_pids(pid)))
                """;
            command.Parameters.AddWithValue("lockType", lockType);
            command.Parameters.Add(
                new NpgsqlParameter("blockerPid", NpgsqlDbType.Integer) { Value = (object?)blockerPid ?? DBNull.Value }
            );
            if (Convert.ToInt32(await command.ExecuteScalarAsync(timeout.Token)) > 0)
            {
                return;
            }

            await Task.Delay(10, timeout.Token);
        }
    }

    [Fact]
    public async Task ARollbackThatThrowsStillClearsTheChangeTracker()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.ActivateAsync(Candidate("catalog", 2m), ct);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var resolver = new UsagePriceResolver(
            _store,
            [new CancelThenThrowCalculator(cancellation)],
            NullLogger<UsagePriceResolver>.Instance
        );
        var repository = new UsageRepository(_db, _store, resolver);
        // The caller's token is cancelled while the write is in flight, so a rollback issued with
        // that token throws and, without a finally, skips the Clear the stale entities depend on.
        _db.UsageEvents.Attach(Event("stale", CostBasis.ListPriceEstimate, 1m, 0m));

        var act = () =>
            repository.RecordEstimatedEventAsync(
                Event("interrupted", CostBasis.ListPriceEstimate, null, null),
                cancellation.Token
            );

        await act.Should().ThrowAsync<InvalidOperationException>("the rollback's own failure must not replace it");
        _db.ChangeTracker.Entries().Should().BeEmpty();
    }

    private sealed class CancelThenThrowCalculator(CancellationTokenSource cancellation) : IProviderPriceCalculator
    {
        public Provider Provider => Provider.OpenAI;

        public UsagePriceQuote Calculate(UsageEvent usage, string normalizedCatalog) =>
            CalculateWithEvidence(usage, normalizedCatalog).Quote;

        public CalculatedPrice CalculateWithEvidence(UsageEvent usage, string normalizedCatalog)
        {
            cancellation.Cancel();
            throw new InvalidOperationException("calculator failed");
        }
    }

    [Fact]
    public async Task CostlessEstimatedReplayPreservesAnOperatorCorrection()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.ActivateAsync(Candidate("catalog", 2m), ct);
        var pricingRepository = new UsageRepository(_db, _store, Resolver(_store));

        // O3: the endpoint nulls the cost for estimated bases and routes here, so the replay's
        // provenance is "the source supplied no cost" even though the resolver then prices it.
        var original = Event("corrected-replay", CostBasis.ListPriceEstimate, null, null);
        (await pricingRepository.RecordEstimatedEventAsync(original, ct))
            .Disposition.Should()
            .Be(RecordEventDisposition.Created);
        (await _db.UsageEvents.AsNoTracking().SingleAsync(ct)).CostUsd.Should().Be(2m);

        await _repository.PatchEventCostAsync(Provider.OpenAI, "repricing-test", "corrected-replay", 3m, ct: ct);

        var replay = Event("corrected-replay", CostBasis.ListPriceEstimate, null, null);
        replay.ObservedAt = Instant.FromUtc(2026, 8, 25, 2, 0);
        (await pricingRepository.RecordEstimatedEventAsync(replay, ct))
            .Disposition.Should()
            .Be(RecordEventDisposition.Unchanged, "only ObservedAt moved once the correction is preserved");

        var saved = await _db.UsageEvents.AsNoTracking().SingleAsync(ct);
        saved
            .CostUsd.Should()
            .Be(3m, "a cost-less estimated replay must not overwrite the operator's figure with the fresh quote");
        saved.CostBasis.Should().Be(CostBasis.ProviderEstimated);
        saved.CorrectedAt.Should().NotBeNull();
    }

    private sealed class ThrowingOnModelCalculator(string boomModel) : IProviderPriceCalculator
    {
        public CalculatedPrice? CalculateWithEvidence(UsageEvent usage, string normalizedCatalog)
        {
            Calculate(usage, normalizedCatalog);
            return new OpenAiPriceCalculator().CalculateWithEvidence(usage, normalizedCatalog);
        }

        private readonly OpenAiPriceCalculator _inner = new();

        public Provider Provider => _inner.Provider;

        public UsagePriceQuote? Calculate(UsageEvent usage, string normalizedCatalog) =>
            usage.Model == boomModel
                ? throw new InvalidOperationException($"No quote for {boomModel}.")
                : _inner.Calculate(usage, normalizedCatalog);
    }

    private sealed class CapturingLogger : ILogger<PricingRepricingService>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }

    private static PricingSnapshotCandidate Candidate(
        string evidence,
        decimal input,
        params (LocalDate EffectiveFrom, decimal Input)[] later
    )
    {
        var entries = new[] { (new LocalDate(2026, 8, 1), input) }
            .Concat(later)
            .Select(entry => new OpenAiPriceEntry(
                "gpt-test",
                ["gpt-test"],
                entry.Item1,
                true,
                "standard",
                "short",
                "global",
                entry.Item2,
                0.5m,
                10m,
                1.25m
            ));
        var catalog = new OpenAiPriceCatalog(
            "USD",
            "https://openai.com/api/pricing/",
            Instant.FromUtc(2026, 8, 25, 0, 0),
            entries.ToArray()
        );
        var normalized = JsonSerializer.Serialize(catalog, JsonOptions);
        return new PricingSnapshotCandidate(
            Provider.OpenAI,
            PricingSourceIds.OpenAi,
            Instant.FromUtc(2026, 8, 25, 0, 0),
            catalog.SourceUrl,
            PricingSnapshotCandidate.ComputeContentHash(evidence, normalized),
            evidence,
            normalized
        );
    }
}
