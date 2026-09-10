using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace AiObservatory.Api.IntegrationTests;

/// <summary>
/// AIO-C1: /api/dev/seed's empty-tables data-preservation guard. This is the fix for a
/// genuine past incident — the seed route used to TRUNCATE every seeded table on every
/// `docker compose up`, silently destroying a self-hoster's Subscription/BudgetRule config
/// (neither of which writes a DailyAggregate) the moment they created one before any usage
/// had aggregated. Own throwaway factory: this test intentionally mutates every seeded
/// table, so it must not share the collection-fixture DB with the endpoint-validation suite.
/// </summary>
[Trait("Category", "Integration")]
public class DevSeedEndpointTests
{
    [Fact]
    public async Task Reseeding_a_database_with_only_a_Subscription_and_BudgetRule_preserves_them()
    {
        await using var factory = new AiObservatoryApiFactory();
        await factory.InitializeAsync();
        using var client = factory.CreateAdminClient();

        // First seed call on a genuinely empty database succeeds.
        var first = await client.PostAsync("/api/dev/seed", content: null, TestContext.Current.CancellationToken);
        first.EnsureSuccessStatusCode();

        // Simulate the exact incident shape: a self-hoster creates a Subscription and a
        // BudgetRule before any DailyAggregate exists. Wipe the seeded DailyAggregates/
        // Insights (as if usage hasn't aggregated yet) but keep the Subscriptions/BudgetRules
        // the first seed call planted, so only those two tables are non-empty.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            await db.DailyAggregates.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
            await db.Insights.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
            await db.UsageEvents.ExecuteDeleteAsync(TestContext.Current.CancellationToken);

            db.Subscriptions.Add(
                new Subscription
                {
                    Provider = Provider.OpenAI,
                    Name = "Self-hoster's own subscription",
                    CostAmount = 42m,
                    Currency = "USD",
                    BillingDay = 5,
                    ActiveFrom = new LocalDate(2026, 1, 1),
                }
            );
            db.BudgetRules.Add(
                new BudgetRule
                {
                    Provider = null,
                    Period = BillingPeriod.Daily,
                    ThresholdGbp = 99m,
                }
            );
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // The baseline contains the two seeded subscriptions and budget rules, plus the
        // subscription and budget rule added directly above.
        var beforeSubscriptionIds = await GetSubscriptionIdsAsync(factory);
        var beforeBudgetRuleIds = await GetBudgetRuleIdsAsync(factory);
        beforeSubscriptionIds.Should().HaveCount(3);
        beforeBudgetRuleIds.Should().HaveCount(3);

        // Act: re-seed. The guard must cover Subscriptions/BudgetRules, not just
        // DailyAggregates — a guard that only checked DailyAggregates would see an "empty"
        // database here and destroy/duplicate the self-hoster's config.
        var second = await client.PostAsync("/api/dev/seed", content: null, TestContext.Current.CancellationToken);
        second.EnsureSuccessStatusCode();
        var body = await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("Already seeded");

        var afterSubscriptionIds = await GetSubscriptionIdsAsync(factory);
        var afterBudgetRuleIds = await GetBudgetRuleIdsAsync(factory);

        afterSubscriptionIds
            .Should()
            .BeEquivalentTo(
                beforeSubscriptionIds,
                "the self-hoster's pre-existing Subscription must survive a re-seed"
            );
        afterBudgetRuleIds
            .Should()
            .BeEquivalentTo(beforeBudgetRuleIds, "the self-hoster's pre-existing BudgetRule must survive a re-seed");
    }

    [Fact]
    public async Task Seeding_a_genuinely_empty_database_populates_all_seeded_tables()
    {
        await using var factory = new AiObservatoryApiFactory();
        await factory.InitializeAsync();
        using var client = factory.CreateAdminClient();

        var response = await client.PostAsync("/api/dev/seed", content: null, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("Seed successful");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        (await db.DailyAggregates.AnyAsync(TestContext.Current.CancellationToken)).Should().BeTrue();
        (await db.Subscriptions.AnyAsync(TestContext.Current.CancellationToken)).Should().BeTrue();
        (await db.BudgetRules.AnyAsync(TestContext.Current.CancellationToken)).Should().BeTrue();
        (await db.Insights.AnyAsync(TestContext.Current.CancellationToken)).Should().BeTrue();
    }

    [Fact]
    public async Task Seeded_aggregates_demonstrate_distinct_source_aware_cost_lanes()
    {
        await using var factory = new AiObservatoryApiFactory();
        await factory.InitializeAsync();
        using var client = factory.CreateAdminClient();

        var response = await client.PostAsync("/api/dev/seed", content: null, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var aggregates = await scope
            .ServiceProvider.GetRequiredService<AiObservatoryDbContext>()
            .DailyAggregates.AsNoTracking()
            .ToListAsync(TestContext.Current.CancellationToken);

        aggregates
            .Should()
            .Contain(a =>
                a.Provider == Provider.Anthropic
                && a.SourceId == UsageSourceIds.DemoSeed
                && a.SourceKind == SourceKind.Synthetic
                && a.UsageScope == UsageScope.Api
                && a.CostBasis == CostBasis.ListPriceEstimate
                && a.CostUsd > 0
            );
        aggregates
            .Should()
            .Contain(a =>
                a.Provider == Provider.Copilot
                && a.SourceId == UsageSourceIds.DemoSeed
                && a.SourceKind == SourceKind.Synthetic
                && a.UsageScope == UsageScope.Subscription
                && a.CostBasis == CostBasis.Notional
                && a.CostUsd > 0
            );
        aggregates
            .Should()
            .Contain(a =>
                a.Provider == Provider.Google
                && a.SourceId == UsageSourceIds.DemoSeed
                && a.SourceKind == SourceKind.Synthetic
                && a.UsageScope == UsageScope.Api
                && a.CostBasis == CostBasis.ListPriceEstimate
                && a.CostUsd > 0
            );
    }

    private static async Task<List<Guid>> GetSubscriptionIdsAsync(AiObservatoryApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        return await db.Subscriptions.Select(s => s.Id).ToListAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<List<Guid>> GetBudgetRuleIdsAsync(AiObservatoryApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        return await db.BudgetRules.Select(r => r.Id).ToListAsync(TestContext.Current.CancellationToken);
    }
}
