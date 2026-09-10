using System.Net;
using AiObservatory.Api.Services.GitHub;
using AiObservatory.Api.Tests.Services;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Spend;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;

namespace AiObservatory.Api.IntegrationTests.Services;

/// <summary>
/// The GitHub org bill is not paid from the account the spend CSV exports, so this sync is
/// the only way any of it reaches the ledger. These cover the two properties the rest of
/// the ledger depends on: every billed line lands exactly once, and re-running converges
/// rather than duplicating.
/// </summary>
[Trait("Category", "Integration")]
public class GitHubBillingSyncServiceTests(AiObservatoryApiFactory factory) : IClassFixture<AiObservatoryApiFactory>
{
    private static readonly Instant Now = Instant.FromUtc(2026, 7, 27, 12, 0);

    /// <summary>0.75 USD->GBP, so a converted figure is obvious by inspection.</summary>
    private const string FxBody = """{"rates":{"GBP":0.75}}""";

    private static GitHubBillingUsageItem Item(string date, string product, string sku, decimal net) =>
        new(
            DateOnly.ParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            product,
            sku,
            net,
            0m,
            net
        );

    /// <summary>
    /// A fresh scope per call, so each sync runs against its own DbContext exactly as the
    /// background worker does — a shared context would hide tracking bugs the real one hits.
    /// </summary>
    private async Task<(int Written, AiObservatoryDbContext Db, IServiceScope Scope)> SyncAsync(
        params GitHubBillingUsageItem[] items
    ) => await SyncAsync(Now, items);

    private async Task<(int Written, AiObservatoryDbContext Db, IServiceScope Scope)> SyncAsync(
        Instant now,
        params GitHubBillingUsageItem[] items
    )
    {
        var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();

        // Disposed here rather than leaked: both outlive the sync only as FX plumbing, and
        // the returned scope/db are what the caller still needs.
        using var fxHttp = new HttpClient(new StubHttpMessageHandler(HttpStatusCode.OK, FxBody));
        using var fxCache = new MemoryCache(new MemoryCacheOptions());

        var sut = new GitHubBillingSyncService(
            new StubBillingClient(items),
            new BillingObservationWriter(
                db,
                new FxRateProvider(fxHttp, fxCache, NullLogger<FxRateProvider>.Instance),
                new FakeClock(now)
            ),
            new FakeClock(now),
            NullLogger<GitHubBillingSyncService>.Instance
        );

        var written = await sut.SyncAsync(TestContext.Current.CancellationToken);
        return (written, db, scope);
    }

    /// <summary>
    /// Isolates each test to its own months. The fixture database is shared across the
    /// class, and every entry is keyed by month, so overlapping months would let one test's
    /// rows satisfy another's assertions.
    /// </summary>
    private static string UniqueSku(string prefix) => $"{prefix} {Guid.NewGuid():N}";

    private static async Task<SpendEntry?> FindAsync(AiObservatoryDbContext db, string sku, CancellationToken ct) =>
        await db
            .SpendEntries.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Source == SpendSource.Api && e.Description == sku, ct);

    [Fact]
    public async Task WritesOneEntryPerBilledLineAndConvertsAtTheMonthStart()
    {
        var sku = UniqueSku("Actions Linux");
        var (written, db, scope) = await SyncAsync(Item("2026-06-01", "actions", sku, 108.494m));
        using var _ = scope;

        written.Should().Be(1);

        var entry = await FindAsync(db, sku, TestContext.Current.CancellationToken);
        entry.Should().NotBeNull();
        entry
            .OccurredOn.Should()
            .Be(
                new LocalDate(2026, 6, 1),
                "GitHub reports usage per month, so the month start is the charge date the rate is frozen at"
            );
        entry.Amount.Should().Be(108.494m);
        entry.Currency.Should().Be("USD", "GitHub bills in dollars whatever the payment method");
        entry.FxRate.Should().Be(0.75m);
        entry.AmountGbp.Should().Be(81.3705m);
        entry.Source.Should().Be(SpendSource.Api);
        entry.SourceId.Should().Be(UsageSourceIds.GitHubBillingApi);
        entry.SourceKind.Should().Be(SourceKind.ProviderApi);
        entry.UsageScope.Should().Be(UsageScope.Mixed);
        entry.CostBasis.Should().Be(CostBasis.Billed);
        entry.ObservedAt.Should().Be(Now);
    }

    [Fact]
    public async Task RetainsFullyDiscountedLinesWithoutCreatingSpend()
    {
        var billed = UniqueSku("Actions Linux");
        var free = UniqueSku("Actions Windows");

        var (written, db, scope) = await SyncAsync(
            Item("2026-05-01", "actions", billed, 9.402m),
            Item("2026-05-01", "actions", free, 0m)
        );
        using var _ = scope;

        written.Should().Be(1);
        (await FindAsync(db, free, TestContext.Current.CancellationToken))
            .Should()
            .BeNull("a zero net line is evidence but not spend");
        var observation = await db
            .BillingObservations.AsNoTracking()
            .SingleAsync(row => row.Sku == free, TestContext.Current.CancellationToken);
        observation.NetAmount.Should().Be(0m);
        observation.SourceId.Should().Be(UsageSourceIds.GitHubBillingApi);
    }

    [Fact]
    public async Task SumsTheSameSkuReportedForMoreThanOneRepository()
    {
        var sku = UniqueSku("Code Quality AI Credits");

        var (written, db, scope) = await SyncAsync(
            // The payload carries repositoryName, so the same SKU can arrive once per repo.
            Item("2026-07-01", "code_quality", sku, 12.01m),
            Item("2026-07-01", "code_quality", sku, 3.99m)
        );
        using var _ = scope;

        written.Should().Be(1, "one billing line per month and SKU, whatever the repository split");

        var entry = await FindAsync(db, sku, TestContext.Current.CancellationToken);
        entry!.Amount.Should().Be(16.00m);
    }

    [Fact]
    public async Task ReRunningWithAGrownAmountUpdatesInPlace()
    {
        var sku = UniqueSku("Actions Linux");

        var first = await SyncAsync(Item("2026-07-01", "actions", sku, 133.602m));
        first.Scope.Dispose();

        // Same month, larger figure — exactly what an open month looks like the next day.
        var (written, db, scope) = await SyncAsync(Item("2026-07-01", "actions", sku, 180.44m));
        using var _ = scope;

        written.Should().Be(1);

        var entries = await db
            .SpendEntries.AsNoTracking()
            .Where(e => e.Source == SpendSource.Api && e.Description == sku)
            .ToListAsync(TestContext.Current.CancellationToken);

        entries
            .Should()
            .ContainSingle(
                "the entry key excludes the amount precisely so an accruing month updates rather than duplicating"
            );
        entries[0].Amount.Should().Be(180.44m);
        entries[0].AmountGbp.Should().Be(135.33m);
    }

    [Fact]
    public async Task ReRunningWithAChangedAmount_refreshes_observation_time()
    {
        var sku = UniqueSku("Actions Linux");
        var firstObservedAt = Instant.FromUtc(2026, 7, 27, 12, 0);
        var correctedObservedAt = Instant.FromUtc(2026, 7, 28, 12, 0);

        var first = await SyncAsync(firstObservedAt, Item("2026-07-01", "actions", sku, 133.602m));
        first.Scope.Dispose();

        var (_, db, scope) = await SyncAsync(correctedObservedAt, Item("2026-07-01", "actions", sku, 180.44m));
        using var _ = scope;

        var entry = await FindAsync(db, sku, TestContext.Current.CancellationToken);
        entry!.ObservedAt.Should().Be(correctedObservedAt);
    }

    [Fact]
    public async Task ReRunningWithNoChangeWritesNothing()
    {
        var sku = UniqueSku("Code Security");
        var item = Item("2026-06-01", "ghas", sku, 30.00m);

        var first = await SyncAsync(item);
        first.Written.Should().Be(1);
        first.Scope.Dispose();

        var (written, _, scope) = await SyncAsync(item);
        using var __ = scope;

        written.Should().Be(0, "a daily sync of a closed month must be a no-op, not a write");
    }

    /// <summary>Returns whatever the test handed it, filtered to the requested year.</summary>
    private sealed class StubBillingClient(IReadOnlyList<GitHubBillingUsageItem> items)
        : GitHubBillingClient(Unused, "test-org", NullLogger<GitHubBillingClient>.Instance)
    {
        /// <summary>
        /// The base constructor demands one, but this override never issues a request, so a
        /// per-instance client would be created and abandoned on every call. One shared
        /// instance for the class instead.
        /// </summary>
        private static readonly HttpClient Unused = new();

        public override Task<IReadOnlyList<GitHubBillingUsageItem>> GetUsageAsync(
            int year,
            CancellationToken ct = default
        ) => Task.FromResult<IReadOnlyList<GitHubBillingUsageItem>>(items.Where(i => i.Date.Year == year).ToList());
    }
}
