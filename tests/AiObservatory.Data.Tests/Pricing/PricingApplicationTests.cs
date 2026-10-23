using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing;
using AiObservatory.Data.Pricing.Catalogs;
using AiObservatory.Data.Repositories;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using Npgsql;

namespace AiObservatory.Data.Tests.Pricing;

[Trait("Category", "Integration")]
public sealed class PricingApplicationTests : IAsyncLifetime
{
    private AiObservatoryDbContext _db = null!;
    private UsageRepository _repository = null!;
    private static readonly Instant Now = Instant.FromUtc(2026, 10, 22, 12, 0);

    public async ValueTask InitializeAsync()
    {
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TEST_DB_CONNECTION"))
        {
            Database = $"aiobs_test_application_{Guid.NewGuid():N}",
        }.ConnectionString;
        _db = new(
            new DbContextOptionsBuilder<AiObservatoryDbContext>().UseNpgsql(connection, o => o.UseNodaTime()).Options
        );
        await _db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var store = new PricingSnapshotStore(_db);
        _repository = new(_db, store, new UsagePriceResolver(store, [], NullLogger<UsagePriceResolver>.Instance));
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
    public async Task EstimatedZero_StoresRuleDerivedApplicationWithCost()
    {
        var usage = Event("zero");
        await _repository.RecordEstimatedEventAsync(usage, TestContext.Current.CancellationToken);
        (await _db.UsageEvents.SingleAsync(TestContext.Current.CancellationToken)).CostUsd.Should().Be(0m);
        var tableCount = await _db
            .Database.SqlQueryRaw<int>(
                "SELECT COUNT(*)::int AS \"Value\" FROM information_schema.tables WHERE table_name = 'PricingApplications'"
            )
            .SingleAsync(TestContext.Current.CancellationToken);
        tableCount.Should().Be(1, "a calculated zero must retain application provenance atomically with its cost");
        var applicationCount = await _db
            .Database.SqlQueryRaw<int>(
                "SELECT COUNT(*)::int AS \"Value\" FROM \"PricingApplications\" WHERE \"Kind\" = 'ruleDerived' AND \"SnapshotId\" IS NULL"
            )
            .SingleAsync(TestContext.Current.CancellationToken);
        applicationCount.Should().Be(1);
    }

    [Fact]
    public async Task Reprice_EqualCostFromNewApplication_AdvancesImmutableRevision()
    {
        var usage = Event("equal");
        await _repository.RecordEstimatedEventAsync(usage, TestContext.Current.CancellationToken);
        var stored = await _db.UsageEvents.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        var application = new ResolvedPriceApplication(
            new(0m, 0m),
            null,
            null,
            "new-zero-rule",
            new(AiObservatory.Audit.Contracts.ServiceTier.Unknown, false, null, null),
            new(2026, 10, 22),
            "2",
            true
        );
        await _repository.UpdateEventPricingWithEvidenceAsync(
            stored,
            application,
            TestContext.Current.CancellationToken
        );
        var current = await _db.UsageEvents.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        current.CurrentPricingRevision.Should().BeGreaterThan(stored.CurrentPricingRevision);
        (
            await _db.PricingApplications.SingleAsync(
                a => a.EventId == current.Id && a.Revision == current.CurrentPricingRevision,
                TestContext.Current.CancellationToken
            )
        )
            .RuleVersion.Should()
            .Be("2");
        (await _db.PricingApplications.CountAsync(TestContext.Current.CancellationToken)).Should().Be(2);
    }

    [Fact]
    public async Task PricingWrite_Rollback_RestoresCostAndApplicationTogether()
    {
        await _repository.RecordEstimatedEventAsync(Event("rollback"), TestContext.Current.CancellationToken);
        var stored = await _db.UsageEvents.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        await using (var tx = await _db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            await _repository.UpdateEventPricingAsync(stored, new(1m, 0m), TestContext.Current.CancellationToken);
            await tx.RollbackAsync(TestContext.Current.CancellationToken);
        }
        _db.ChangeTracker.Clear();
        (await _db.UsageEvents.SingleAsync(TestContext.Current.CancellationToken)).CostUsd.Should().Be(0m);
        (await _db.PricingApplications.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task ExplicitCorrectionAndSourceCost_SupersedeCalculatedProvenance()
    {
        await _repository.RecordEstimatedEventAsync(Event("authority"), TestContext.Current.CancellationToken);
        await _repository.PatchEventCostAsync(
            Provider.OpenAI,
            UsageSourceIds.LegacyApi,
            "authority",
            2m,
            ct: TestContext.Current.CancellationToken
        );
        (await _db.PricingApplications.CountAsync(TestContext.Current.CancellationToken))
            .Should()
            .Be(2, "one operator patch applies exactly one correction revision");
        (await _db.PricingApplications.CountAsync(a => a.Kind == "source", TestContext.Current.CancellationToken))
            .Should()
            .Be(0, "an operator patch is not a source assertion");
        var row = await _db.UsageEvents.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        (
            await _db.PricingApplications.SingleAsync(
                a => a.EventId == row.Id && a.Revision == row.CurrentPricingRevision,
                TestContext.Current.CancellationToken
            )
        )
            .Kind.Should()
            .Be("correction");
        var source = Event("authority");
        source.CostUsd = 3m;
        source.CostBasis = CostBasis.ProviderEstimated;
        await _repository.RecordEventAsync(source, TestContext.Current.CancellationToken);
        row = await _db.UsageEvents.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        var application = await _db.PricingApplications.SingleAsync(
            a => a.EventId == row.Id && a.Revision == row.CurrentPricingRevision,
            TestContext.Current.CancellationToken
        );
        application.Kind.Should().Be("source");
        application.SnapshotId.Should().BeNull();
        row.CostUsd.Should().Be(3m);
    }

    [Fact]
    public async Task ApplicationHistory_RejectsMutation()
    {
        await _repository.RecordEstimatedEventAsync(Event("immutable"), TestContext.Current.CancellationToken);
        var mutate = async () =>
            await _db.Database.ExecuteSqlRawAsync(
                "UPDATE \"PricingApplications\" SET \"Kind\" = 'source'",
                TestContext.Current.CancellationToken
            );
        await mutate.Should().ThrowAsync<PostgresException>();
        (await _db.PricingApplications.SingleAsync(TestContext.Current.CancellationToken))
            .Kind.Should()
            .Be("ruleDerived");
    }

    [Fact]
    public async Task RetainedFallback_StoresActuallySelectedCatalogAndEntry()
    {
        var entry = new OpenAiPriceEntry(
            "synthetic",
            ["synthetic"],
            new(2026, 10, 1),
            true,
            "standard",
            "short",
            "global",
            2m,
            0.5m,
            10m,
            3m
        );
        var first = new PricingSnapshot
        {
            Provider = Provider.OpenAI,
            SourceId = "openai-pricing",
            RetrievedAt = Now,
            ContentHash = new string('a', 64),
            NormalizedCatalog = PricingCatalogJson.Serialize(
                new OpenAiPriceCatalog("USD", "https://example.com/pricing", Now, [entry])
            ),
        };
        var second = new PricingSnapshot
        {
            Provider = Provider.OpenAI,
            SourceId = "openai-pricing",
            RetrievedAt = Now + Duration.FromHours(1),
            IsActive = true,
            ContentHash = new string('b', 64),
            NormalizedCatalog = PricingCatalogJson.Serialize(
                new OpenAiPriceCatalog(
                    "USD",
                    "https://example.com/pricing",
                    Now,
                    [entry with { ModelPrefix = "retired", Aliases = ["retired"] }]
                )
            ),
        };
        _db.PricingSnapshots.AddRange(first, second);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var store = new PricingSnapshotStore(_db);
        var resolver = new UsagePriceResolver(
            store,
            [new OpenAiPriceCalculator()],
            NullLogger<UsagePriceResolver>.Instance
        );
        var usage = Event("fallback");
        usage.InputTokens = 1_000_000;
        usage.RawPayload = """{"processing":"standard","context":"short","region":"global"}""";
        var resolved = await resolver.ResolveWithEvidenceAsync(usage, TestContext.Current.CancellationToken);
        resolved.Should().NotBeNull();
        resolved.SnapshotId.Should().Be(first.Id);
        resolved.RateKey.Should().Be(PricingEvidence.RateKey(entry));
        var repository = new UsageRepository(_db, store, resolver);
        await repository.RecordEstimatedEventAsync(usage, TestContext.Current.CancellationToken);
        var application = await _db.PricingApplications.SingleAsync(TestContext.Current.CancellationToken);
        application.SnapshotId.Should().Be(first.Id);
        application.SnapshotHash.Should().Be(first.ContentHash);
        application.CostUsd.Should().Be(2m);
        application.RateKey.Should().Be(resolved.RateKey);
    }

    [Fact]
    public async Task ApplicationInsertFailure_RollsBackEventCostAndAggregate()
    {
        await _db.Database.ExecuteSqlRawAsync(
            """
            CREATE FUNCTION reject_test_application() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'synthetic_write_failure'; END $$;
            CREATE TRIGGER reject_test_application BEFORE INSERT ON "PricingApplications"
            FOR EACH ROW EXECUTE FUNCTION reject_test_application();
            """,
            TestContext.Current.CancellationToken
        );
        var write = async () =>
            await _repository.RecordEstimatedEventAsync(Event("failure"), TestContext.Current.CancellationToken);
        await write.Should().ThrowAsync<DbUpdateException>();
        (await _db.UsageEvents.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        (await _db.PricingApplications.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        (await _db.DailyAggregates.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Theory]
    [InlineData("{}", null, null, false)]
    [InlineData(
        "{\"cache_creation\":{\"ephemeral_5m_input_tokens\":0,\"ephemeral_1h_input_tokens\":100}}",
        0L,
        100L,
        true
    )]
    [InlineData(
        "{\"cache_creation\":{\"ephemeral_5m_input_tokens\":100,\"ephemeral_1h_input_tokens\":0}}",
        100L,
        0L,
        false
    )]
    public async Task AnthropicNotionalCacheWrites_PersistInputsAndApplicationForIndependentDurationChoice(
        string raw,
        long? observed5M,
        long? observed1H,
        bool exact
    )
    {
        var ct = TestContext.Current.CancellationToken;
        var entry = new AnthropicPriceEntry(
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
        );
        var snapshot = new PricingSnapshot
        {
            Provider = Provider.Anthropic,
            SourceId = PricingSourceIds.Claude,
            ContentHash = new string('a', 64),
            RetrievedAt = Now,
            IsActive = true,
            NormalizedCatalog = PricingCatalogJson.Serialize(
                new AnthropicPriceCatalog("USD", "https://example.com/pricing", Now, [entry])
            ),
        };
        _db.PricingSnapshots.Add(snapshot);
        await _db.SaveChangesAsync(ct);
        var store = new PricingSnapshotStore(_db);
        var resolver = new UsagePriceResolver(
            store,
            [new AnthropicPriceCalculator()],
            NullLogger<UsagePriceResolver>.Instance
        );
        var repository = new UsageRepository(_db, store, resolver);
        await repository.RecordEstimatedEventAsync(
            new()
            {
                Provider = Provider.Anthropic,
                Model = "synthetic",
                OccurredAt = Now,
                ObservedAt = Now,
                IngestedAt = Now,
                CostBasis = CostBasis.Notional,
                CacheWriteTokens = 100,
                CacheWrite1hTokens = 100,
                RawPayload = raw,
            },
            ct
        );
        var stored = await _db.UsageEvents.AsNoTracking().SingleAsync(ct);
        var application = await _db.PricingApplications.SingleAsync(ct);
        PricingEvidence
            .ObserveCacheDurations(stored)
            .Should()
            .Be(new AiObservatory.Audit.Contracts.CacheDurationObservation(observed5M, observed1H));
        stored.CacheWrite1hTokens.Should().Be(100);
        application.RevisionDigest.Should().Be(PricingEvidence.RevisionDigest(stored));
        application.SnapshotId.Should().Be(snapshot.Id);
        application.RateKey.Should().Be(PricingEvidence.RateKey(entry));
        application.CostUsd.Should().Be(exact ? 0.0004m : 0.00025m);
        stored.CostUsd.Should().Be(application.CostUsd);
    }

    [Theory]
    [InlineData(ClaudeContextFixture.Lower, CostBasis.Notional, 23.25, -2.25)]
    [InlineData(ClaudeContextFixture.Upper, CostBasis.Notional, 61.5, -22.5)]
    [InlineData(ClaudeContextFixture.Lower, CostBasis.ListPriceEstimate, 23.25, -2.25)]
    [InlineData(ClaudeContextFixture.Upper, CostBasis.ListPriceEstimate, 61.5, -22.5)]
    public async Task ClaudeActivationRepricesCompleteBandAndAggregateOnceWithoutChangingSourceFacts(
        string context,
        CostBasis basis,
        decimal expectedCost,
        decimal expectedSavings
    )
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new PricingSnapshotStore(_db);
        var resolver = new UsagePriceResolver(
            store,
            [new AnthropicPriceCalculator()],
            NullLogger<UsagePriceResolver>.Instance
        );
        var repository = new UsageRepository(_db, store, resolver);
        var usage = ClaudeContextFixture.Event(ClaudeContextFixture.Raw(context));
        usage.CostBasis = basis;
        usage.OutputTokens = 1_000_000;
        usage.CacheReadTokens = 1_000_000;
        usage.CacheWriteTokens = 2_000_000;
        usage.CacheWrite1hTokens = 1_000_000;
        usage.ThoughtTokens = 123;
        usage.CostUsd = 9m;
        usage.CacheSavingsUsd = 1m;
        await repository.RecordEventAsync(usage, ct);
        var before = await _db.UsageEvents.AsNoTracking().SingleAsync(ct);
        var oldApplications = await _db.PricingApplications.AsNoTracking().ToListAsync(ct);
        var catalog = ClaudeContextFixture.Catalog();
        var normalized = PricingCatalogJson.Serialize(catalog);
        var candidate = new PricingSnapshotCandidate(
            Provider.Anthropic,
            PricingSourceIds.Claude,
            catalog.RetrievedAt,
            catalog.SourceUrl,
            PricingSnapshotCandidate.ComputeContentHash("synthetic-complete-bands", normalized),
            "synthetic-complete-bands",
            normalized
        );
        var repricing = new PricingRepricingService(_db, repository, resolver, store);
        (
            await store.ActivateAsync(
                candidate,
                ct,
                (_, token) => repricing.RepriceProviderAsync(Provider.Anthropic, token)
            )
        )
            .Should()
            .Be(PricingActivationResult.Activated);

        var after = await _db.UsageEvents.AsNoTracking().SingleAsync(ct);
        after
            .Should()
            .BeEquivalentTo(
                before,
                options =>
                    options
                        .Excluding(row => row.CostUsd)
                        .Excluding(row => row.CacheSavingsUsd)
                        .Excluding(row => row.CurrentPricingRevision)
            );
        after.CostUsd.Should().Be(expectedCost);
        after.CacheSavingsUsd.Should().Be(expectedSavings);
        after.CurrentPricingRevision.Should().BeGreaterThan(before.CurrentPricingRevision);
        var history = await _db.PricingApplications.AsNoTracking().OrderBy(row => row.Revision).ToListAsync(ct);
        history.Should().HaveCount(oldApplications.Count + 1);
        history.Take(oldApplications.Count).Should().BeEquivalentTo(oldApplications);
        var application = history[^1];
        application.SnapshotHash.Should().Be(candidate.ContentHash);
        application.SnapshotId.Should().Be((await store.GetActiveAsync(PricingSourceIds.Claude, ct))!.Id);
        application.RateKey.Should().Be(PricingEvidence.RateKey(ClaudeContextFixture.Entry(context)));
        using var dimensions = System.Text.Json.JsonDocument.Parse(application.Dimensions);
        dimensions.RootElement.GetProperty("context").GetString().Should().Be(context);
        application.RuleVersion.Should().Be("1");
        application.CostUsd.Should().Be(expectedCost);
        application.CacheSavingsUsd.Should().Be(expectedSavings);
        var aggregate = await _db.DailyAggregates.AsNoTracking().SingleAsync(ct);
        aggregate.InputTokens.Should().Be(1_000_000);
        aggregate.OutputTokens.Should().Be(1_000_000);
        aggregate.CacheReadTokens.Should().Be(1_000_000);
        aggregate.CacheWriteTokens.Should().Be(2_000_000);
        aggregate.CacheWrite1hTokens.Should().Be(1_000_000);
        aggregate.CostUsd.Should().Be(expectedCost);
        aggregate.CacheSavingsUsd.Should().Be(expectedSavings);
        aggregate.UnknownCostCount.Should().Be(0);
        aggregate.UnknownCacheSavingsCount.Should().Be(0);
        aggregate.RequestCount.Should().Be(1);

        (
            await store.ActivateAsync(
                candidate,
                ct,
                (_, token) => repricing.RepriceProviderAsync(Provider.Anthropic, token)
            )
        )
            .Should()
            .Be(PricingActivationResult.Unchanged);
        (await _db.PricingApplications.AsNoTracking().OrderBy(row => row.Revision).ToListAsync(ct))
            .Should()
            .BeEquivalentTo(history);
        (await _db.DailyAggregates.AsNoTracking().SingleAsync(ct)).Should().BeEquivalentTo(aggregate);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClaudeActivationAdmitsClaimedHistoricalFlatQuoteThroughRepricingTransaction(bool correctFamily)
    {
        var ct = TestContext.Current.CancellationToken;
        var flat = ClaudeContextFixture.Entry(null) with
        {
            EffectiveFrom = new(2026, 9, 1),
            ModelPrefix = correctFamily ? "claude-haiku-5-5" : "claude-haiku-5",
            Aliases = ["claude-haiku-5-5"],
            Input = correctFamily ? 2m : 99m,
        };
        var retained = new PricingSnapshot
        {
            Provider = Provider.Anthropic,
            SourceId = PricingSourceIds.Claude,
            RetrievedAt = Instant.FromUtc(2026, 9, 1, 12, 0),
            IsActive = true,
            ContentHash = new string('a', 64),
            NormalizedCatalog = PricingCatalogJson.Serialize(
                ClaudeContextFixture.Catalog(flat) with
                {
                    RetrievedAt = Instant.FromUtc(2026, 9, 1, 12, 0),
                }
            ),
        };
        _db.PricingSnapshots.Add(retained);
        await _db.SaveChangesAsync(ct);
        var store = new PricingSnapshotStore(_db);
        var resolver = new UsagePriceResolver(
            store,
            [new AnthropicPriceCalculator()],
            NullLogger<UsagePriceResolver>.Instance
        );
        var repository = new UsageRepository(_db, store, resolver);
        var usage = ClaudeContextFixture.Event(ClaudeContextFixture.Raw(ClaudeContextFixture.Upper));
        usage.CostBasis = CostBasis.ListPriceEstimate;
        usage.OccurredAt = Instant.FromUtc(2026, 9, 20, 0, 0);
        usage.OutputTokens = 1_000_000;
        usage.CacheReadTokens = 1_000_000;
        usage.CacheWriteTokens = 2_000_000;
        usage.CacheWrite1hTokens = 1_000_000;
        usage.ThoughtTokens = 123;
        (await resolver.ResolveWithEvidenceAsync(usage, ct)).Should().BeNull();
        await repository.RecordEstimatedEventAsync(usage, ct);
        var before = await _db.UsageEvents.AsNoTracking().SingleAsync(ct);
        before.CostUsd.Should().BeNull();
        before.CacheSavingsUsd.Should().BeNull();
        var oldAggregate = await _db.DailyAggregates.AsNoTracking().SingleAsync(ct);
        oldAggregate.UnknownCostCount.Should().Be(1);
        oldAggregate.UnknownCacheSavingsCount.Should().Be(1);
        var oldApplications = await _db.PricingApplications.AsNoTracking().OrderBy(row => row.Revision).ToListAsync(ct);
        oldApplications.Should().OnlyContain(row => row.Kind != "catalog");

        var catalog = ClaudeContextFixture.Catalog();
        var normalized = PricingCatalogJson.Serialize(catalog);
        var candidate = new PricingSnapshotCandidate(
            Provider.Anthropic,
            PricingSourceIds.Claude,
            catalog.RetrievedAt,
            catalog.SourceUrl,
            PricingSnapshotCandidate.ComputeContentHash("synthetic-historical-activation", normalized),
            "synthetic-historical-activation",
            normalized
        );
        var repricing = new PricingRepricingService(_db, repository, resolver, store);
        (
            await store.ActivateAsync(
                candidate,
                ct,
                (_, token) => repricing.RepriceProviderAsync(Provider.Anthropic, token)
            )
        )
            .Should()
            .Be(PricingActivationResult.Activated);
        var after = await _db.UsageEvents.AsNoTracking().SingleAsync(ct);
        if (!correctFamily)
        {
            after.Should().BeEquivalentTo(before);
            after.RawPayload.Should().Be(before.RawPayload);
            PricingEvidence.SourceDimensions(after).Context.Should().Be(ClaudeContextFixture.Upper);
            after.CostUsd.Should().BeNull();
            after.CacheSavingsUsd.Should().BeNull();
            (await _db.PricingApplications.AsNoTracking().OrderBy(row => row.Revision).ToListAsync(ct))
                .Should()
                .BeEquivalentTo(oldApplications);
            (await _db.PricingApplications.CountAsync(row => row.Kind == "catalog", ct)).Should().Be(0);
            (await _db.DailyAggregates.AsNoTracking().SingleAsync(ct)).Should().BeEquivalentTo(oldAggregate);
            (await store.GetActiveAsync(PricingSourceIds.Claude, ct))!.ContentHash.Should().Be(candidate.ContentHash);
            return;
        }
        after
            .Should()
            .BeEquivalentTo(
                before,
                options =>
                    options
                        .Excluding(row => row.CostUsd)
                        .Excluding(row => row.CacheSavingsUsd)
                        .Excluding(row => row.CurrentPricingRevision)
            );
        after.RawPayload.Should().Be(before.RawPayload);
        PricingEvidence.SourceDimensions(after).Context.Should().Be(ClaudeContextFixture.Upper);
        after.CostUsd.Should().Be(23.25m);
        after.CacheSavingsUsd.Should().Be(-2.25m);
        after.CurrentPricingRevision.Should().BeGreaterThan(before.CurrentPricingRevision);
        var history = await _db.PricingApplications.AsNoTracking().OrderBy(row => row.Revision).ToListAsync(ct);
        history.Should().HaveCount(oldApplications.Count + 1);
        history.Take(oldApplications.Count).Should().BeEquivalentTo(oldApplications);
        var application = history[^1];
        application.SnapshotId.Should().Be(retained.Id);
        application.SnapshotHash.Should().Be(retained.ContentHash);
        application.RateKey.Should().Be(PricingEvidence.RateKey(flat));
        application.PricingDate.Should().Be(new DateOnly(2026, 9, 20));
        application.RuleVersion.Should().Be("1");
        application.CostUsd.Should().Be(23.25m);
        application.CacheSavingsUsd.Should().Be(-2.25m);
        AiObservatory
            .Audit.Serialization.AuditCanonicalJson.Deserialize<AiObservatory.Audit.Contracts.PricingDimensions>(
                application.Dimensions
            )
            .Context.Should()
            .BeNull();
        var aggregate = await _db.DailyAggregates.AsNoTracking().SingleAsync(ct);
        aggregate
            .Should()
            .BeEquivalentTo(
                oldAggregate,
                options =>
                    options
                        .Excluding(row => row.CostUsd)
                        .Excluding(row => row.CacheSavingsUsd)
                        .Excluding(row => row.UnknownCostCount)
                        .Excluding(row => row.UnknownCacheSavingsCount)
            );
        aggregate.CostUsd.Should().Be(23.25m);
        aggregate.CacheSavingsUsd.Should().Be(-2.25m);
        aggregate.UnknownCostCount.Should().Be(0);
        aggregate.UnknownCacheSavingsCount.Should().Be(0);
        aggregate.RequestCount.Should().Be(1);
        (await store.GetActiveAsync(PricingSourceIds.Claude, ct))!.ContentHash.Should().Be(candidate.ContentHash);
    }

    [Fact]
    public async Task MissingClaudeBandRetainsKnownCostWithoutNewSuccessfulApplication()
    {
        var ct = TestContext.Current.CancellationToken;
        var snapshot = new PricingSnapshot
        {
            Provider = Provider.Anthropic,
            SourceId = PricingSourceIds.Claude,
            RetrievedAt = ClaudeContextFixture.Now,
            ContentHash = new string('d', 64),
            IsActive = true,
            NormalizedCatalog = ClaudeContextFixture.Json(),
        };
        _db.PricingSnapshots.Add(snapshot);
        await _db.SaveChangesAsync(ct);
        var store = new PricingSnapshotStore(_db);
        var resolver = new UsagePriceResolver(
            store,
            [new AnthropicPriceCalculator()],
            NullLogger<UsagePriceResolver>.Instance
        );
        var repository = new UsageRepository(_db, store, resolver);
        var known = ClaudeContextFixture.Event(ClaudeContextFixture.Raw(null));
        known.CostUsd = 9m;
        var unknown = ClaudeContextFixture.Event(ClaudeContextFixture.Raw(null));
        var billed = ClaudeContextFixture.Event(ClaudeContextFixture.Raw(ClaudeContextFixture.Lower));
        billed.CostBasis = CostBasis.Billed;
        billed.CostUsd = 7m;
        var provider = ClaudeContextFixture.Event(ClaudeContextFixture.Raw(ClaudeContextFixture.Upper));
        provider.CostBasis = CostBasis.ProviderEstimated;
        provider.CostUsd = 8m;
        foreach (var usage in new[] { known, unknown, billed, provider })
        {
            await repository.RecordEventAsync(usage, ct);
        }
        var before = await _db.PricingApplications.CountAsync(ct);
        var knownRevision = (
            await _db.UsageEvents.AsNoTracking().SingleAsync(u => u.Id == known.Id, ct)
        ).CurrentPricingRevision;
        await new PricingRepricingService(_db, repository, resolver, store).RepriceProviderAsync(
            Provider.Anthropic,
            ct
        );
        var rows = await _db.UsageEvents.AsNoTracking().ToDictionaryAsync(u => u.Id, ct);
        rows[known.Id].CostUsd.Should().Be(9m);
        rows[known.Id].CurrentPricingRevision.Should().Be(knownRevision);
        rows[unknown.Id].CostUsd.Should().BeNull();
        rows[billed.Id].CostUsd.Should().Be(7m);
        rows[provider.Id].CostUsd.Should().Be(8m);
        rows.Values.Should().OnlyContain(u => u.InputTokens == 1_000_000);
        (await _db.PricingApplications.CountAsync(ct)).Should().Be(before);
        (await _db.PricingApplications.CountAsync(a => a.SnapshotId != null, ct)).Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContextChangeInvalidatesRepricingPublication(bool changeMarker)
    {
        var ct = TestContext.Current.CancellationToken;
        var snapshot = new PricingSnapshot
        {
            Provider = Provider.Anthropic,
            SourceId = PricingSourceIds.Claude,
            RetrievedAt = ClaudeContextFixture.Now,
            ContentHash = new string('c', 64),
            IsActive = true,
            NormalizedCatalog = ClaudeContextFixture.Json(),
        };
        _db.PricingSnapshots.Add(snapshot);
        await _db.SaveChangesAsync(ct);
        var store = new PricingSnapshotStore(_db);
        var resolver = new UsagePriceResolver(
            store,
            [new AnthropicPriceCalculator()],
            NullLogger<UsagePriceResolver>.Instance
        );
        var repository = new UsageRepository(_db, store, resolver);
        var usage = ClaudeContextFixture.Event(ClaudeContextFixture.Raw(ClaudeContextFixture.Lower));
        usage.CostUsd = 9m;
        await repository.RecordEventAsync(usage, ct);
        var priced = await _db.UsageEvents.AsNoTracking().SingleAsync(ct);
        var quote = await resolver.ResolveWithEvidenceAsync(priced, ct);
        quote.Should().NotBeNull();
        quote.RateKey.Should().Be(PricingEvidence.RateKey(ClaudeContextFixture.Entry(ClaudeContextFixture.Lower)));
        var applications = await _db.PricingApplications.CountAsync(ct);
        var correctedRaw = changeMarker
            ? ClaudeContextFixture.Raw(ClaudeContextFixture.Lower).Replace("\"1\"", "\"2\"", StringComparison.Ordinal)
            : ClaudeContextFixture.Raw(ClaudeContextFixture.Upper);
        // Interleave a source correction after the unlocked read and before publication.
        // Only RawPayload changes: every other fence input, revision and amount stays equal.
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"UsageEvents\" SET \"RawPayload\" = {correctedRaw}::jsonb WHERE \"Id\" = {priced.Id}",
            ct
        );
        await repository.UpdateEventPricingWithEvidenceAsync(priced, quote, ct);
        var current = await _db.UsageEvents.AsNoTracking().SingleAsync(ct);
        current.CostUsd.Should().Be(9m);
        current.CurrentPricingRevision.Should().Be(priced.CurrentPricingRevision);
        (await _db.PricingApplications.CountAsync(ct)).Should().Be(applications);
        PricingEvidence.RevisionDigest(current).Should().NotBe(PricingEvidence.RevisionDigest(priced));
        var correctedQuote = await resolver.ResolveWithEvidenceAsync(current, ct);
        if (changeMarker)
        {
            correctedQuote.Should().BeNull();
            return;
        }
        correctedQuote.Should().NotBeNull();
        correctedQuote
            .RateKey.Should()
            .Be(PricingEvidence.RateKey(ClaudeContextFixture.Entry(ClaudeContextFixture.Upper)));
        await repository.UpdateEventPricingWithEvidenceAsync(current, correctedQuote, ct);
        (await _db.UsageEvents.AsNoTracking().SingleAsync(ct)).CostUsd.Should().Be(4m);
        var application = await _db.PricingApplications.SingleAsync(
            a => a.EventId == current.Id && a.Revision > priced.CurrentPricingRevision,
            ct
        );
        application.RateKey.Should().Be(correctedQuote.RateKey);
        application.SnapshotId.Should().Be(snapshot.Id);
        application.RuleVersion.Should().Be("1");
    }

    private static UsageEvent Event(string key) =>
        new()
        {
            Provider = Provider.OpenAI,
            Model = "synthetic",
            EventKey = key,
            OccurredAt = Now,
            ObservedAt = Now,
            IngestedAt = Now,
            CostBasis = CostBasis.ListPriceEstimate,
        };
}
