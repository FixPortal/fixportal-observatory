using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing;
using AiObservatory.Data.Pricing.Catalogs;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;
using Npgsql;

namespace AiObservatory.Data.Tests.Pricing;

[Trait("Category", "Integration")]
public sealed class PricingSnapshotStoreTests : IAsyncLifetime
{
    private static readonly Instant RetrievedAt = Instant.FromUtc(2026, 8, 24, 20, 0);
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(
        JsonSerializerDefaults.Web
    ).ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);

    private string _connectionString = null!;
    private DbContextOptions<AiObservatoryDbContext> _options = null!;
    private AiObservatoryDbContext _db = null!;
    private PricingSnapshotStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        var baseConnection =
            Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
            ?? "Host=localhost;Database=aiobs_test;Username=postgres;Password=postgres";
        _connectionString = new NpgsqlConnectionStringBuilder(baseConnection)
        {
            Database = $"aiobs_test_pricing_{Guid.NewGuid():N}",
        }.ConnectionString;
        _options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(_connectionString, options => options.UseNodaTime())
            .Options;
        _db = new AiObservatoryDbContext(_options);
        await _db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        _store = new PricingSnapshotStore(_db);
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
    public async Task ActivateRetainsImmutableEvidenceAndChangesOnlyTheActiveSnapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = Candidate("# exact first-party Markdown\n\n| model | price |\n", 1m);

        (await _store.ActivateAsync(first, ct)).Should().Be(PricingActivationResult.Activated);
        (
            await _store.ActivateAsync(
                first,
                ct,
                (_, _) => throw new InvalidOperationException("unchanged activation invoked its callback")
            )
        )
            .Should()
            .Be(PricingActivationResult.Unchanged);
        var second = Candidate("second exact document", 2m, retrievedAt: RetrievedAt.Plus(Duration.FromMinutes(1)));
        (await _store.ActivateAsync(second, ct)).Should().Be(PricingActivationResult.Activated);

        var snapshots = await _db.PricingSnapshots.AsNoTracking().OrderBy(x => x.RetrievedAt).ToListAsync(ct);
        snapshots.Should().HaveCount(2);
        snapshots.Single(x => x.IsActive).ContentHash.Should().Be(second.ContentHash);
        snapshots
            .Single(x => !x.IsActive)
            .Should()
            .BeEquivalentTo(
                new { first.ContentHash, RawEvidence = "# exact first-party Markdown\n\n| model | price |\n" }
            );
        (await _store.GetActiveAsync(PricingSourceIds.OpenAi, ct))!.ContentHash.Should().Be(second.ContentHash);
    }

    [Fact]
    public async Task ActivateReactivatesAHistoricSnapshotWhenTheSourceRevertsToIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = Candidate("first document", 1m);
        var second = Candidate("second document", 2m, retrievedAt: RetrievedAt.Plus(Duration.FromMinutes(1)));
        await _store.ActivateAsync(first, ct);
        await _store.ActivateAsync(second, ct);

        var reverted = first;
        (await _store.ActivateAsync(reverted, ct)).Should().Be(PricingActivationResult.Activated);

        var snapshots = await _db.PricingSnapshots.AsNoTracking().ToListAsync(ct);
        snapshots.Should().HaveCount(2);
        var active = snapshots.Single(x => x.IsActive);
        active.ContentHash.Should().Be(first.ContentHash);
        active.RetrievedAt.Should().Be(first.RetrievedAt);
        (await _store.GetActiveAsync(PricingSourceIds.OpenAi, ct))!.ContentHash.Should().Be(first.ContentHash);

        // Reactivating the already-active document is still a no-op.
        (await _store.ActivateAsync(reverted, ct))
            .Should()
            .Be(PricingActivationResult.Unchanged);
        (await _db.PricingSnapshots.AsNoTracking().CountAsync(ct)).Should().Be(2);
    }

    [Fact]
    public async Task ActivateRollsBackSnapshotAndCallbackWritesWhenCallbackFails()
    {
        var ct = TestContext.Current.CancellationToken;
        var original = Candidate("original", 1m);
        await _store.ActivateAsync(original, ct);

        var act = () =>
            _store.ActivateAsync(
                Candidate("rejected replacement", 2m),
                ct,
                async (_, callbackCt) =>
                {
                    _db.SourceSyncStates.Add(
                        new SourceSyncState
                        {
                            SourceId = "callback-side-effect",
                            ExpectedRefreshIntervalSeconds = 86_400,
                        }
                    );
                    await _db.SaveChangesAsync(callbackCt);
                    throw new InvalidOperationException("repricing failed");
                }
            );

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("repricing failed");
        _db.ChangeTracker.Entries().Should().BeEmpty();
        (await _db.PricingSnapshots.AsNoTracking().SingleAsync(ct)).ContentHash.Should().Be(original.ContentHash);
        (await _store.GetActiveAsync(PricingSourceIds.OpenAi, ct))!.ContentHash.Should().Be(original.ContentHash);
        (await _db.SourceSyncStates.AsNoTracking().CountAsync(ct)).Should().Be(0);

        var recovery = Candidate("recovery", 3m, retrievedAt: RetrievedAt.Plus(Duration.FromMinutes(2)));
        (await _store.ActivateAsync(recovery, ct)).Should().Be(PricingActivationResult.Activated);
        (await _store.GetActiveAsync(PricingSourceIds.OpenAi, ct))!.ContentHash.Should().Be(recovery.ContentHash);
    }

    [Fact]
    public async Task ActivateValidatesTrustInputsBeforeChangingStoredState()
    {
        var ct = TestContext.Current.CancellationToken;
        var original = Candidate("original", 1m);
        await _store.ActivateAsync(original, ct);
        var transactions = new TransactionCounter();
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(_connectionString, builder => builder.UseNodaTime())
            .AddInterceptors(transactions)
            .Options;
        await using var validationDb = new AiObservatoryDbContext(options);
        var invalid = Candidate("invalid", 2m) with { SourceId = PricingSourceIds.Claude };

        var act = () => new PricingSnapshotStore(validationDb).ActivateAsync(invalid, ct);

        await act.Should().ThrowAsync<ArgumentException>();
        transactions.Started.Should().Be(0);
        var saved = await _db.PricingSnapshots.AsNoTracking().SingleAsync(ct);
        saved.ContentHash.Should().Be(original.ContentHash);
        saved.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task ConcurrentActivationsLeaveOneActiveSnapshotPerSource()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var firstDb = new AiObservatoryDbContext(_options);
        await using var secondDb = new AiObservatoryDbContext(_options);

        var second = Candidate("second", 2m, retrievedAt: RetrievedAt.Plus(Duration.FromMinutes(1)));
        var results = await RunContendedActivationsAsync(firstDb, secondDb, Candidate("first", 1m), second, ct);

        results.Should().OnlyContain(result => result == PricingActivationResult.Activated);
        var snapshots = await _db.PricingSnapshots.AsNoTracking().ToListAsync(ct);
        snapshots.Should().HaveCount(2);
        snapshots.Count(x => x.IsActive).Should().Be(1);
        snapshots.Single(x => x.IsActive).ContentHash.Should().Be(second.ContentHash);
    }

    [Fact]
    public async Task ConcurrentIdenticalActivationsReturnOneUnchangedWithoutDuplicatingEvidence()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var firstDb = new AiObservatoryDbContext(_options);
        await using var secondDb = new AiObservatoryDbContext(_options);
        var candidate = Candidate("same evidence", 1m);

        var results = await RunContendedActivationsAsync(firstDb, secondDb, candidate, candidate, ct);

        results.Should().ContainSingle(result => result == PricingActivationResult.Activated);
        results.Should().ContainSingle(result => result == PricingActivationResult.Unchanged);
        (await _db.PricingSnapshots.AsNoTracking().CountAsync(ct)).Should().Be(1);
    }

    [Fact]
    public async Task PostgreSqlEnforcesSnapshotUniquenessAndColumnTypes()
    {
        var ct = TestContext.Current.CancellationToken;
        var normalized = ValidCatalogJson(1m);
        _db.PricingSnapshots.AddRange(Snapshot('a', false, normalized), Snapshot('a', false, normalized));

        var duplicateHash = () => _db.SaveChangesAsync(ct);

        await duplicateHash.Should().ThrowAsync<DbUpdateException>();
        _db.ChangeTracker.Clear();
        _db.PricingSnapshots.AddRange(Snapshot('a', true, normalized), Snapshot('b', true, normalized));

        var duplicateActive = () => _db.SaveChangesAsync(ct);

        await duplicateActive.Should().ThrowAsync<DbUpdateException>();
        _db.ChangeTracker.Clear();
        var columnTypes = await _db
            .Database.SqlQueryRaw<ColumnType>(
                """
                SELECT "column_name" AS "Name", "data_type" AS "Type"
                FROM information_schema.columns
                WHERE "table_name" = 'PricingSnapshots'
                  AND "column_name" IN ('RawEvidence', 'NormalizedCatalog')
                ORDER BY "column_name"
                """
            )
            .ToListAsync(ct);
        columnTypes
            .Should()
            .BeEquivalentTo([new ColumnType("NormalizedCatalog", "jsonb"), new ColumnType("RawEvidence", "text")]);
    }

    [Fact]
    public async Task GetCatalogForDateUsesTheActiveCatalogAndHonoursFutureEffectiveEntries()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.ActivateAsync(Candidate("first", 1m), ct);
        await _store.ActivateAsync(
            Candidate(
                "second",
                2m,
                new LocalDate(2026, 8, 24),
                new LocalDate(2026, 9, 1),
                RetrievedAt.Plus(Duration.FromMinutes(1))
            ),
            ct
        );

        // Both entries carry assumed (non-provider-declared) effective dates, so the earliest
        // window is treated as open-ended backwards: history predating the first fetch prices.
        var july = await _store.GetCatalogForDateAsync(Provider.OpenAI, new LocalDate(2026, 7, 31), ct);
        var august = await _store.GetCatalogForDateAsync(Provider.OpenAI, new LocalDate(2026, 8, 31), ct);
        var september = await _store.GetCatalogForDateAsync(Provider.OpenAI, new LocalDate(2026, 9, 1), ct);

        july.Should().NotBeNull();
        august.Should().NotBeNull();
        september.Should().NotBeNull();
        var catalog = JsonSerializer.Deserialize<OpenAiPriceCatalog>(september.NormalizedCatalog, JsonOptions)!;
        catalog.Resolve("gpt-5.4", "standard", "short", "global", new LocalDate(2026, 7, 31))!.Input.Should().Be(1m);
        catalog.Resolve("gpt-5.4", "standard", "short", "global", new LocalDate(2026, 8, 31))!.Input.Should().Be(1m);
        catalog.Resolve("gpt-5.4", "standard", "short", "global", new LocalDate(2026, 9, 1))!.Input.Should().Be(2m);
        august.ContentHash.Should().Be(september.ContentHash);
    }

    [Fact]
    public async Task GetCatalogForDateFallsBackToTheNewestRetainedSnapshotThatCoversTheUsageDate()
    {
        var ct = TestContext.Current.CancellationToken;
        var oldOnly = Candidate("old-only", 1m, new LocalDate(2026, 8, 1));
        var newOnly = Candidate(
            "new-only",
            2m,
            new LocalDate(2026, 9, 1),
            retrievedAt: RetrievedAt.Plus(Duration.FromMinutes(1))
        );
        await _store.ActivateAsync(oldOnly, ct);
        await _store.ActivateAsync(newOnly, ct);

        var august = await _store.GetCatalogForDateAsync(Provider.OpenAI, new LocalDate(2026, 8, 15), ct);
        var september = await _store.GetCatalogForDateAsync(Provider.OpenAI, new LocalDate(2026, 9, 15), ct);

        august!.ContentHash.Should().Be(oldOnly.ContentHash);
        september!.ContentHash.Should().Be(newOnly.ContentHash);
        (await _store.GetActiveAsync(PricingSourceIds.OpenAi, ct))!.ContentHash.Should().Be(newOnly.ContentHash);
    }

    [Fact]
    public async Task GetCatalogForDatePricesPreCatalogHistoryFromTheEarliestAssumedSnapshot()
    {
        // Two live-refresh catalogs, each stamping its entries with the fetch date (assumed,
        // not provider-declared). Usage predating both must price from the EARLIEST assumed
        // window: the newest fetched rates did not exist yet at that usage date.
        var ct = TestContext.Current.CancellationToken;
        var august = Candidate("august catalog", 1m, new LocalDate(2026, 8, 24));
        var september = Candidate(
            "september catalog",
            2m,
            new LocalDate(2026, 9, 24),
            retrievedAt: RetrievedAt.Plus(Duration.FromDays(31))
        );
        await _store.ActivateAsync(august, ct);
        await _store.ActivateAsync(september, ct);

        var july = await _store.GetCatalogForDateAsync(Provider.OpenAI, new LocalDate(2026, 7, 31), ct);

        july!.ContentHash.Should().Be(august.ContentHash);
        var catalog = JsonSerializer.Deserialize<OpenAiPriceCatalog>(july.NormalizedCatalog, JsonOptions)!;
        catalog.Resolve("gpt-5.4", "standard", "short", "global", new LocalDate(2026, 7, 31))!.Input.Should().Be(1m);
    }

    [Fact]
    public async Task GetCatalogForDateRestoresAReactivatedCatalogForEstimatedPricing()
    {
        // Reactivating an earlier catalog deliberately does not re-stamp RetrievedAt; ranking
        // retrieval time above activation would keep the superseded newer catalog pricing
        // everything, silently diverging from the reported activation state.
        var ct = TestContext.Current.CancellationToken;
        var first = Candidate("first document", 1m, new LocalDate(2026, 8, 1));
        var second = Candidate(
            "second document",
            2m,
            new LocalDate(2026, 9, 1),
            retrievedAt: RetrievedAt.Plus(Duration.FromMinutes(1))
        );
        await _store.ActivateAsync(first, ct);
        await _store.ActivateAsync(second, ct);
        (await _store.ActivateAsync(first, ct)).Should().Be(PricingActivationResult.Activated);

        var october = await _store.GetCatalogForDateAsync(Provider.OpenAI, new LocalDate(2026, 10, 15), ct);

        october!.ContentHash.Should().Be(first.ContentHash);
        (await _store.GetActiveAsync(PricingSourceIds.OpenAi, ct))!.ContentHash.Should().Be(first.ContentHash);
    }

    [Theory]
    [InlineData("kimi-highSpeed")]
    [InlineData("google-contextThreshold")]
    [InlineData("entry-effectiveDateIsProviderDeclared")]
    [InlineData("catalog-retrievedAt")]
    public async Task ActivateRejectsMissingRequiredNormalizedFieldsBeforeStartingATransaction(string fieldCase)
    {
        var ct = TestContext.Current.CancellationToken;
        var transactions = new TransactionCounter();
        await using var validationDb = CreateContext(transactions);
        var candidate = CandidateMissingRequiredField(fieldCase);

        var act = () => new PricingSnapshotStore(validationDb).ActivateAsync(candidate, ct);

        await act.Should().ThrowAsync<ArgumentException>();
        transactions.Started.Should().Be(0);
    }

    [Fact]
    public async Task ActivateAcceptsExplicitFalseAndZeroRequiredNormalizedDimensions()
    {
        var ct = TestContext.Current.CancellationToken;
        var date = new LocalDate(2026, 8, 24);
        var kimiSource = "https://platform.kimi.ai/docs/pricing/chat-k3";
        var googleSource = "https://cloud.google.com/billing/catalog";
        var kimi = CandidateFor(
            Provider.Moonshot,
            PricingSourceIds.Kimi,
            kimiSource,
            "explicit false",
            JsonSerializer.Serialize(
                new KimiPriceCatalog(
                    "USD",
                    kimiSource,
                    RetrievedAt,
                    [new("kimi", ["kimi"], date, false, 0.1m, 1m, 2m, false, null)]
                ),
                JsonOptions
            )
        );
        var google = CandidateFor(
            Provider.Google,
            PricingSourceIds.GoogleCloudCatalog,
            googleSource,
            "explicit zero",
            JsonSerializer.Serialize(
                new GooglePriceCatalog(
                    "USD",
                    googleSource,
                    RetrievedAt,
                    [GoogleEntry(date) with { ContextThreshold = 0 }]
                ),
                JsonOptions
            )
        );

        (await _store.ActivateAsync(kimi, ct)).Should().Be(PricingActivationResult.Activated);
        (await _store.ActivateAsync(google, ct)).Should().Be(PricingActivationResult.Activated);
    }

    [Fact]
    public async Task ActivateRejectsNonCanonicalEvidenceHashBeforeStartingATransaction()
    {
        var ct = TestContext.Current.CancellationToken;
        var transactions = new TransactionCounter();
        await using var validationDb = CreateContext(transactions);
        var candidate = Candidate("uppercase hash", 1m);
        candidate = candidate with { ContentHash = candidate.ContentHash.ToUpperInvariant() };

        var act = () => new PricingSnapshotStore(validationDb).ActivateAsync(candidate, ct);

        await act.Should().ThrowAsync<ArgumentException>();
        transactions.Started.Should().Be(0);
    }

    [Fact]
    public async Task ActivateRejectsAReusedHashWithChangedEvidenceAndCatalogBeforeStartingATransaction()
    {
        var ct = TestContext.Current.CancellationToken;
        var original = Candidate("original evidence", 1m);
        await _store.ActivateAsync(original, ct);
        var transactions = new TransactionCounter();
        await using var validationDb = CreateContext(transactions);
        var changed = Candidate("changed evidence", 2m) with { ContentHash = original.ContentHash };
        changed.NormalizedCatalog.Should().NotBe(original.NormalizedCatalog);

        var act = () => new PricingSnapshotStore(validationDb).ActivateAsync(changed, ct);

        await act.Should().ThrowAsync<ArgumentException>();
        transactions.Started.Should().Be(0);
    }

    [Fact]
    public async Task ActivateRejectsAnOversizeSourceUrlBeforeStartingATransaction()
    {
        var ct = TestContext.Current.CancellationToken;
        var transactions = new TransactionCounter();
        await using var validationDb = CreateContext(transactions);
        var sourceUrl = "https://example.com/" + new string('a', 2048);
        var candidate = Candidate("oversize URL", 1m, sourceUrl: sourceUrl);

        var act = () => new PricingSnapshotStore(validationDb).ActivateAsync(candidate, ct);

        await act.Should().ThrowAsync<ArgumentException>();
        transactions.Started.Should().Be(0);
    }

    [Fact]
    public async Task ActivateCallbackCancellationRollsBackAndPropagatesWithACleanTracker()
    {
        var original = Candidate("original", 1m);
        await _store.ActivateAsync(original, TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        var replacement = Candidate("cancelled", 2m, retrievedAt: RetrievedAt.Plus(Duration.FromMinutes(1)));

        var act = () =>
            _store.ActivateAsync(
                replacement,
                cancellation.Token,
                async (_, callbackToken) =>
                {
                    _db.SourceSyncStates.Add(
                        new SourceSyncState
                        {
                            SourceId = "cancelled-callback-side-effect",
                            ExpectedRefreshIntervalSeconds = 86_400,
                        }
                    );
                    await _db.SaveChangesAsync(callbackToken);
                    await cancellation.CancelAsync();
                    callbackToken.ThrowIfCancellationRequested();
                }
            );

        var thrown = await act.Should().ThrowAsync<OperationCanceledException>();
        thrown.Which.CancellationToken.Should().Be(cancellation.Token);
        _db.ChangeTracker.Entries().Should().BeEmpty();
        await using var verifier = new AiObservatoryDbContext(_options);
        var testToken = TestContext.Current.CancellationToken;
        (await verifier.PricingSnapshots.AsNoTracking().SingleAsync(testToken))
            .ContentHash.Should()
            .Be(original.ContentHash);
        (await verifier.SourceSyncStates.AsNoTracking().CountAsync(testToken)).Should().Be(0);
    }

    [Fact]
    public void ResolveCannotSelectAFutureDeclaredEntryForAnEarlierUsageDate()
    {
        // A provider-declared effective date always gates; only assumed dates are open-ended
        // backwards (see EffectiveWindow).
        var catalog = new OpenAiPriceCatalog(
            "USD",
            "https://example.com/pricing",
            RetrievedAt,
            [OpenAiEntry(new LocalDate(2026, 9, 1), 2m) with { EffectiveDateIsProviderDeclared = true }]
        );

        catalog.Resolve("gpt-5.4", "standard", "short", "global", new LocalDate(2026, 8, 31)).Should().BeNull();
    }

    [Fact]
    public void ResolveTreatsTheEarliestAssumedWindowAsOpenEndedBackwards()
    {
        var catalog = new OpenAiPriceCatalog(
            "USD",
            "https://example.com/pricing",
            RetrievedAt,
            [OpenAiEntry(new LocalDate(2026, 8, 24), 1m), OpenAiEntry(new LocalDate(2026, 9, 1), 2m)]
        );

        catalog.Resolve("gpt-5.4", "standard", "short", "global", new LocalDate(2026, 7, 31))!.Input.Should().Be(1m);
        catalog.Resolve("gpt-5.4", "standard", "short", "global", new LocalDate(2026, 8, 31))!.Input.Should().Be(1m);
        catalog.Resolve("gpt-5.4", "standard", "short", "global", new LocalDate(2026, 9, 1))!.Input.Should().Be(2m);
    }

    [Fact]
    public async Task ActivateRejectsARetrievedAtThatDisagreesWithTheEmbeddedCatalog()
    {
        var ct = TestContext.Current.CancellationToken;
        var mismatched = Candidate("mismatched clock", 1m) with
        {
            RetrievedAt = RetrievedAt.Plus(Duration.FromHours(3)),
        };

        var act = () => _store.ActivateAsync(mismatched, ct);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*normalized pricing catalog is invalid*");
    }

    [Fact]
    public async Task ActivateTreatsACorrectedCatalogFromUnchangedEvidenceAsNewContent()
    {
        // The normaliser is code: a fix that changes the NormalizedCatalog produced from the
        // same raw evidence must activate (and so reprice), not short-circuit as Unchanged.
        var ct = TestContext.Current.CancellationToken;
        var original = Candidate("same evidence", 1m);
        (await _store.ActivateAsync(original, ct)).Should().Be(PricingActivationResult.Activated);

        var corrected = CandidateFor(
            Provider.OpenAI,
            PricingSourceIds.OpenAi,
            original.SourceUrl,
            "same evidence",
            ValidCatalogJson(1.5m),
            original.RetrievedAt
        );
        corrected.RawEvidence.Should().Be(original.RawEvidence);
        corrected.ContentHash.Should().NotBe(original.ContentHash);

        (await _store.ActivateAsync(corrected, ct)).Should().Be(PricingActivationResult.Activated);

        var snapshots = await _db.PricingSnapshots.AsNoTracking().ToListAsync(ct);
        snapshots.Should().HaveCount(2);
        snapshots.Single(x => x.IsActive).ContentHash.Should().Be(corrected.ContentHash);
    }

    [Fact]
    public async Task ActivateTreatsARefetchThatOnlyRestampsFetchDerivedDatesAsUnchanged()
    {
        // A daily re-fetch of unchanged provider evidence re-stamps the catalog's retrievedAt
        // and every assumed effectiveFrom with the new fetch date. Neither is content, so the
        // refresh must compare equal instead of inserting a duplicate snapshot and repricing.
        var ct = TestContext.Current.CancellationToken;
        var original = Candidate("unchanged evidence", 1m, new LocalDate(2026, 8, 24));
        (await _store.ActivateAsync(original, ct)).Should().Be(PricingActivationResult.Activated);
        var refetched = Candidate(
            "unchanged evidence",
            1m,
            new LocalDate(2026, 9, 24),
            retrievedAt: RetrievedAt.Plus(Duration.FromDays(31))
        );

        (await _store.ActivateAsync(refetched, ct)).Should().Be(PricingActivationResult.Unchanged);

        var stored = await _db.PricingSnapshots.AsNoTracking().SingleAsync(ct);
        stored.ContentHash.Should().Be(original.ContentHash);
        stored.RetrievedAt.Should().Be(original.RetrievedAt);
    }

    [Fact]
    public void ProviderCatalogValidatorsRejectInvalidProviderSpecificShapes()
    {
        var date = new LocalDate(2026, 8, 24);
        var source = "https://example.com/pricing";
        var invalidOpenAi = new OpenAiPriceCatalog(
            "EUR",
            source,
            RetrievedAt,
            [new("gpt", ["gpt"], date, false, "", "short", "global", 1m, 0.1m, 2m, null)]
        );
        var invalidAnthropic = new AnthropicPriceCatalog(
            "USD",
            source,
            RetrievedAt,
            [new("claude", ["claude"], date, false, 1m, 2m, 0.1m, 1.25m, 2m, null, null, null, null, 0m)]
        );
        var invalidKimi = new KimiPriceCatalog(
            "USD",
            source,
            RetrievedAt,
            [new("kimi", ["kimi"], date, false, 0m, 1m, 2m, false, null)]
        );
        var invalidGoogle = new GooglePriceCatalog(
            "USD",
            source,
            RetrievedAt,
            [GoogleEntry(date) with { Service = "" }]
        );

        ((Action)invalidOpenAi.Validate).Should().Throw<InvalidDataException>();
        ((Action)invalidAnthropic.Validate).Should().Throw<InvalidDataException>();
        ((Action)invalidKimi.Validate).Should().Throw<InvalidDataException>();
        ((Action)invalidGoogle.Validate).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void ProviderCatalogValidatorsAcceptCompleteExplicitShapes()
    {
        var date = new LocalDate(2026, 8, 24);
        var source = "https://example.com/pricing";
        var catalogs = new Action[]
        {
            new OpenAiPriceCatalog("USD", source, RetrievedAt, [OpenAiEntry(date, 1m)]).Validate,
            new AnthropicPriceCatalog(
                "USD",
                source,
                RetrievedAt,
                [new("claude", ["claude"], date, false, 1m, 2m, 0.1m, 1.25m, 2m, 0.5m, 1m, 2m, 4m, 1.1m)]
            ).Validate,
            new KimiPriceCatalog(
                "USD",
                source,
                RetrievedAt,
                [new("kimi", ["kimi"], date, false, 0.1m, 1m, 2m, false, 0.6m)]
            ).Validate,
            new GooglePriceCatalog("USD", source, RetrievedAt, [GoogleEntry(date)]).Validate,
        };

        catalogs.Should().AllSatisfy(validate => validate.Should().NotThrow());
    }

    [Fact]
    public void GeminiDeveloperCatalogRejectsCachedInputPricedAboveFreshInput()
    {
        var catalog = new GeminiDeveloperPriceCatalog(
            "USD",
            "https://ai.google.dev/gemini-api/docs/pricing",
            RetrievedAt,
            [
                new(
                    "gemini-3.1-pro-preview",
                    ["gemini-3.1-pro-preview"],
                    new LocalDate(2026, 2, 19),
                    false,
                    "standard",
                    "short",
                    2m,
                    3m,
                    12m
                ),
            ]
        );

        ((Action)catalog.Validate).Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData("taxonomy-enum")]
    [InlineData("global-regions")]
    [InlineData("regional-regions")]
    [InlineData("regional-two-regions")]
    [InlineData("multi-regional-one-region")]
    [InlineData("aggregation-level")]
    [InlineData("aggregation-interval")]
    [InlineData("tier-start")]
    [InlineData("currency-conversion")]
    [InlineData("effective-provenance")]
    [InlineData("effective-time")]
    [InlineData("base-factor")]
    [InlineData("money-nanos-bound")]
    [InlineData("money-sign")]
    [InlineData("money-rate")]
    public void GoogleCatalogValidatorRejectsParserImpossibleShapes(string mutation)
    {
        var date = new LocalDate(2026, 8, 24);
        var valid = GoogleEntry(date);
        var entry = mutation switch
        {
            "taxonomy-enum" => valid with { GeoTaxonomyType = "WORLD" },
            "global-regions" => valid with
            {
                Region = "global",
                GeoTaxonomyType = "GLOBAL",
                ServiceRegions = ["global"],
                GeoTaxonomyRegions = ["global"],
            },
            "regional-regions" => valid with { GeoTaxonomyRegions = [] },
            "regional-two-regions" => valid with { GeoTaxonomyRegions = ["us", "europe-west1"] },
            "multi-regional-one-region" => valid with { GeoTaxonomyType = "MULTI_REGIONAL" },
            "aggregation-level" => valid with { AggregationLevel = "TEAM" },
            "aggregation-interval" => valid with { AggregationInterval = "HOURLY" },
            "tier-start" => valid with { TierStartUsageAmount = -1m },
            "currency-conversion" => valid with { CurrencyConversionRate = 0.9m },
            "effective-provenance" => valid with { EffectiveDateIsProviderDeclared = false },
            "effective-time" => valid with { EffectiveTime = valid.EffectiveTime + Duration.FromDays(1) },
            "base-factor" => valid with { BaseUnitConversionFactor = 2m },
            "money-nanos-bound" => valid with { UnitPriceNanos = 1_000_000_000 },
            "money-sign" => valid with { UnitPriceUnits = 1, UnitPriceNanos = -1, Rate = 999_999.999m },
            "money-rate" => valid with { Rate = 2m },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        var catalog = new GooglePriceCatalog("USD", "https://example.com/pricing", RetrievedAt, [entry]);

        ((Action)catalog.Validate).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void GoogleCatalogValidatorAllowsCaseDistinctSkuIdsInTheSameDimensions()
    {
        var entry = GoogleEntry(new LocalDate(2026, 8, 24));
        var catalog = new GooglePriceCatalog(
            "USD",
            "https://example.com/pricing",
            RetrievedAt,
            [entry, entry with { SkuId = "SKU" }]
        );

        ((Action)catalog.Validate).Should().NotThrow();
    }

    [Fact]
    public void GoogleCatalogValidatorRejectsSameSkuOverlapAcrossDimensionCaseVariants()
    {
        var entry = GoogleEntry(new LocalDate(2026, 8, 24));
        var catalog = new GooglePriceCatalog(
            "USD",
            "https://example.com/pricing",
            RetrievedAt,
            [entry, entry with { Modality = "TEXT" }]
        );

        ((Action)catalog.Validate).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void ProviderCatalogValidatorRejectsDuplicateOrUnorderedEffectiveWindows()
    {
        var first = OpenAiEntry(new LocalDate(2026, 9, 1), 2m);
        var earlier = OpenAiEntry(new LocalDate(2026, 8, 24), 1m);
        var duplicate = OpenAiEntry(new LocalDate(2026, 9, 1), 3m);

        ((Action)new OpenAiPriceCatalog("USD", "https://example.com", RetrievedAt, [first, earlier]).Validate)
            .Should()
            .Throw<InvalidDataException>();
        ((Action)new OpenAiPriceCatalog("USD", "https://example.com", RetrievedAt, [first, duplicate]).Validate)
            .Should()
            .Throw<InvalidDataException>();
    }

    private static PricingSnapshotCandidate Candidate(
        string raw,
        decimal input,
        LocalDate? effectiveFrom = null,
        LocalDate? future = null,
        Instant? retrievedAt = null,
        string sourceUrl = "https://developers.openai.com/api/docs/pricing.md"
    )
    {
        var retrieved = retrievedAt ?? RetrievedAt;
        return CandidateFor(
            Provider.OpenAI,
            PricingSourceIds.OpenAi,
            sourceUrl,
            raw,
            ValidCatalogJson(input, effectiveFrom, future, retrieved, sourceUrl),
            retrieved
        );
    }

    private static string ValidCatalogJson(
        decimal input,
        LocalDate? effectiveFrom = null,
        LocalDate? future = null,
        Instant? retrievedAt = null,
        string sourceUrl = "https://developers.openai.com/api/docs/pricing.md"
    )
    {
        var entries = new List<OpenAiPriceEntry>
        {
            OpenAiEntry(effectiveFrom ?? new LocalDate(2026, 8, 24), future is null ? input : 1m),
        };
        if (future is not null)
        {
            entries.Add(OpenAiEntry(future.Value, input));
        }

        return JsonSerializer.Serialize(
            new OpenAiPriceCatalog("USD", sourceUrl, retrievedAt ?? RetrievedAt, entries),
            JsonOptions
        );
    }

    private static PricingSnapshotCandidate CandidateFor(
        Provider provider,
        string sourceId,
        string sourceUrl,
        string raw,
        string normalizedCatalog,
        Instant? retrievedAt = null
    ) =>
        new(
            provider,
            sourceId,
            retrievedAt ?? RetrievedAt,
            sourceUrl,
            PricingSnapshotCandidate.ComputeContentHash(raw, normalizedCatalog),
            raw,
            normalizedCatalog
        );

    private static PricingSnapshotCandidate CandidateMissingRequiredField(string fieldCase)
    {
        var date = new LocalDate(2026, 8, 24);
        return fieldCase switch
        {
            "kimi-highSpeed" => WithoutRequiredField(
                Provider.Moonshot,
                PricingSourceIds.Kimi,
                "https://platform.kimi.ai/docs/pricing/chat-k3",
                "missing Kimi HighSpeed",
                new KimiPriceCatalog(
                    "USD",
                    "https://platform.kimi.ai/docs/pricing/chat-k3",
                    RetrievedAt,
                    [new("kimi", ["kimi"], date, false, 0.1m, 1m, 2m, false, null)]
                ),
                "highSpeed",
                true
            ),
            "google-contextThreshold" => WithoutRequiredField(
                Provider.Google,
                PricingSourceIds.GoogleCloudCatalog,
                "https://cloud.google.com/billing/catalog",
                "missing Google ContextThreshold",
                new GooglePriceCatalog(
                    "USD",
                    "https://cloud.google.com/billing/catalog",
                    RetrievedAt,
                    [GoogleEntry(date) with { ContextThreshold = 0 }]
                ),
                "contextThreshold",
                true
            ),
            "entry-effectiveDateIsProviderDeclared" => WithoutRequiredField(
                Provider.OpenAI,
                PricingSourceIds.OpenAi,
                "https://developers.openai.com/api/docs/pricing.md",
                "missing effective-date provenance",
                new OpenAiPriceCatalog(
                    "USD",
                    "https://developers.openai.com/api/docs/pricing.md",
                    RetrievedAt,
                    [OpenAiEntry(date, 1m)]
                ),
                "effectiveDateIsProviderDeclared",
                true
            ),
            "catalog-retrievedAt" => WithoutRequiredField(
                Provider.OpenAI,
                PricingSourceIds.OpenAi,
                "https://developers.openai.com/api/docs/pricing.md",
                "missing catalog retrieval time",
                new OpenAiPriceCatalog(
                    "USD",
                    "https://developers.openai.com/api/docs/pricing.md",
                    RetrievedAt,
                    [OpenAiEntry(date, 1m)]
                ),
                "retrievedAt",
                false
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(fieldCase)),
        };
    }

    private static PricingSnapshotCandidate WithoutRequiredField<TCatalog>(
        Provider provider,
        string sourceId,
        string sourceUrl,
        string raw,
        TCatalog catalog,
        string property,
        bool entryProperty
    )
    {
        var root = JsonSerializer.SerializeToNode(catalog, JsonOptions)!.AsObject();
        var target = entryProperty ? root["entries"]![0]!.AsObject() : root;
        target.Remove(property).Should().BeTrue();
        return CandidateFor(provider, sourceId, sourceUrl, raw, root.ToJsonString(JsonOptions));
    }

    private AiObservatoryDbContext CreateContext(DbTransactionInterceptor interceptor) =>
        new(
            new DbContextOptionsBuilder<AiObservatoryDbContext>()
                .UseNpgsql(_connectionString, builder => builder.UseNodaTime())
                .AddInterceptors(interceptor)
                .Options
        );

    private async Task<PricingActivationResult[]> RunContendedActivationsAsync(
        AiObservatoryDbContext firstDb,
        AiObservatoryDbContext secondDb,
        PricingSnapshotCandidate firstCandidate,
        PricingSnapshotCandidate secondCandidate,
        CancellationToken cancellationToken
    )
    {
        var callbackEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new PricingSnapshotStore(firstDb).ActivateAsync(
            firstCandidate,
            cancellationToken,
            async (_, callbackToken) =>
            {
                callbackEntered.TrySetResult(true);
                await releaseCallback.Task.WaitAsync(callbackToken);
            }
        );
        await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        var second = new PricingSnapshotStore(secondDb).ActivateAsync(secondCandidate, cancellationToken);
        try
        {
            await WaitForBlockedAdvisoryLockAsync(cancellationToken);
        }
        finally
        {
            releaseCallback.TrySetResult(true);
        }

        return await Task.WhenAll(first, second);
    }

    private async Task WaitForBlockedAdvisoryLockAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(timeout.Token);
        while (true)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted";
            if (Convert.ToInt32(await command.ExecuteScalarAsync(timeout.Token)) > 0)
            {
                return;
            }

            await Task.Delay(10, timeout.Token);
        }
    }

    private static OpenAiPriceEntry OpenAiEntry(LocalDate effectiveFrom, decimal input) =>
        new("gpt-5.4", ["gpt-5.4"], effectiveFrom, false, "standard", "short", "global", input, 0.25m, 10m, null);

    private static GooglePriceEntry GoogleEntry(LocalDate effectiveFrom) =>
        new(
            "Gemini",
            "sku",
            "services/test/skus/sku",
            "Synthetic Google catalog test entry",
            ["gemini"],
            effectiveFrom,
            true,
            effectiveFrom.AtMidnight().InZoneStrictly(DateTimeZone.Utc).ToInstant(),
            "us",
            "REGIONAL",
            ["us"],
            ["us"],
            "text",
            "standard",
            "none",
            128_000,
            "token",
            "token",
            "token",
            "token",
            1m,
            1_000_000m,
            0m,
            "USD",
            0,
            1000,
            "ACCOUNT",
            "DAILY",
            1,
            1m,
            1m
        );

    private static PricingSnapshot Snapshot(char hash, bool active, string normalized) =>
        new()
        {
            Provider = Provider.OpenAI,
            SourceId = PricingSourceIds.OpenAi,
            RetrievedAt = RetrievedAt,
            SourceUrl = "https://developers.openai.com/api/docs/pricing.md",
            ContentHash = new string(hash, 64),
            RawEvidence = "raw Markdown",
            NormalizedCatalog = normalized,
            IsActive = active,
        };

    private sealed record ColumnType(string Name, string Type);

    private sealed class TransactionCounter : DbTransactionInterceptor
    {
        public int Started { get; private set; }

        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection,
            TransactionStartingEventData eventData,
            InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default
        )
        {
            Started++;
            return ValueTask.FromResult(result);
        }
    }
}
