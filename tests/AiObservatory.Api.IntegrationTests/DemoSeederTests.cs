using AiObservatory.Data;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace AiObservatory.Api.IntegrationTests;

/// <summary>
/// The demo reset wipes the seeded tables and reseeds in one transaction. These tests pin the
/// three ways that could go wrong in front of a visitor: leaving catalog data behind or
/// deleting it, leaving the dashboard empty after a failed seed, and duplicating rows when two
/// resets overlap.
/// </summary>
[Trait("Category", "Integration")]
public class DemoSeederTests
{
    private sealed class ThrowingClock : IClock
    {
        public Instant GetCurrentInstant() => throw new InvalidOperationException("clock failure");
    }

    private static async Task<(
        int Aggregates,
        int Subscriptions,
        int BudgetRules,
        int Insights,
        int Events,
        int Spend
    )> CountSeededAsync(AiObservatoryApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        var ct = TestContext.Current.CancellationToken;
        return (
            await db.DailyAggregates.CountAsync(ct),
            await db.Subscriptions.CountAsync(ct),
            await db.BudgetRules.CountAsync(ct),
            await db.Insights.CountAsync(ct),
            await db.UsageEvents.CountAsync(ct),
            await db.SpendEntries.CountAsync(ct)
        );
    }

    [Fact]
    public async Task ClearAsync_empties_every_seeded_table_and_keeps_the_vendor_catalog()
    {
        await using var factory = new AiObservatoryApiFactory();
        await factory.InitializeAsync();
        using var client = factory.CreateAdminClient();
        (
            await client.PostAsync("/api/dev/seed", content: null, TestContext.Current.CancellationToken)
        ).EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        var vendorsBefore = await db.SpendVendors.CountAsync(TestContext.Current.CancellationToken);

        await DemoSeeder.ClearAsync(db, TestContext.Current.CancellationToken);

        (await DemoSeeder.HasAnyDataAsync(db, TestContext.Current.CancellationToken)).Should().BeFalse();
        (await db.SpendVendors.CountAsync(TestContext.Current.CancellationToken))
            .Should()
            .Be(vendorsBefore)
            .And.BeGreaterThan(0, "the vendor catalog is migration data, not seed data");
    }

    [Fact]
    public async Task ResetAsync_restores_the_same_seed_after_the_data_was_changed()
    {
        await using var factory = new AiObservatoryApiFactory();
        await factory.InitializeAsync();
        using var client = factory.CreateAdminClient();
        (
            await client.PostAsync("/api/dev/seed", content: null, TestContext.Current.CancellationToken)
        ).EnsureSuccessStatusCode();
        var seeded = await CountSeededAsync(factory);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            await db.Subscriptions.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
            await db.Insights.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }

        using (var scope = factory.Services.CreateScope())
        {
            await DemoSeeder.ResetAsync(
                scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>(),
                scope.ServiceProvider.GetRequiredService<IClock>(),
                TestContext.Current.CancellationToken
            );
        }

        (await CountSeededAsync(factory)).Should().Be(seeded);
    }

    [Fact]
    public async Task ResetAsync_when_the_seed_fails_leaves_the_previous_data_untouched()
    {
        await using var factory = new AiObservatoryApiFactory();
        await factory.InitializeAsync();
        using var client = factory.CreateAdminClient();
        (
            await client.PostAsync("/api/dev/seed", content: null, TestContext.Current.CancellationToken)
        ).EnsureSuccessStatusCode();
        var seeded = await CountSeededAsync(factory);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            var act = () => DemoSeeder.ResetAsync(db, new ThrowingClock(), TestContext.Current.CancellationToken);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        (await CountSeededAsync(factory)).Should().Be(seeded, "a failed reset must roll back the wipe");
    }

    [Fact]
    public async Task ResetAsync_run_twice_concurrently_leaves_one_copy_of_the_seed()
    {
        await using var factory = new AiObservatoryApiFactory();
        await factory.InitializeAsync();
        using var client = factory.CreateAdminClient();
        (
            await client.PostAsync("/api/dev/seed", content: null, TestContext.Current.CancellationToken)
        ).EnsureSuccessStatusCode();
        var seeded = await CountSeededAsync(factory);

        async Task ResetOnceAsync()
        {
            using var scope = factory.Services.CreateScope();
            await DemoSeeder.ResetAsync(
                scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>(),
                scope.ServiceProvider.GetRequiredService<IClock>(),
                TestContext.Current.CancellationToken
            );
        }

        await Task.WhenAll(ResetOnceAsync(), ResetOnceAsync());

        (await CountSeededAsync(factory)).Should().Be(seeded);
    }
}
