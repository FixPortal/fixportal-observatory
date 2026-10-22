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
