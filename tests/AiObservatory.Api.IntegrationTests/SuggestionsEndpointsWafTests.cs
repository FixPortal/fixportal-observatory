using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace AiObservatory.Api.IntegrationTests;

[Trait("Category", "Integration")]
public class SuggestionsEndpointsWafTests(AiObservatoryApiFactory factory) : IClassFixture<AiObservatoryApiFactory>
{
    private const string UnusedKey = "plan.unused:Meta";

    // This class owns its host and database, so the Meta plan it seeds cannot leak into another
    // class and nothing else can have put Meta rows there first.
    private async Task SeedUnusedMetaPlanAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        // The database is shared by the whole run, so start every test from a clean row for the key.
        await db.Suggestions.Where(s => s.Key == UnusedKey).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        if (await db.Subscriptions.AnyAsync(s => s.Provider == Provider.Meta, TestContext.Current.CancellationToken))
        {
            return;
        }
        db.Subscriptions.Add(
            new Subscription
            {
                Provider = Provider.Meta,
                Name = "Suggestions test plan",
                CostAmount = 10m,
                Currency = "GBP",
                BillingInterval = SubscriptionBillingInterval.Monthly,
                BillingDay = 1,
                ActiveFrom = new LocalDate(2026, 1, 1),
            }
        );
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string query = "")
    {
        var ct = TestContext.Current.CancellationToken;
        return await client.GetFromJsonAsync<JsonElement>($"/api/suggestions{query}", ct);
    }

    private static JsonElement? Find(JsonElement body, string key) =>
        body.GetProperty("suggestions")
            .EnumerateArray()
            .Where(s => s.GetProperty("key").GetString() == key)
            .Select(s => (JsonElement?)s)
            .FirstOrDefault();

    [Fact]
    public async Task The_fx_client_never_reaches_the_live_service()
    {
        using var scope = factory.Services.CreateScope();
        var fx = scope.ServiceProvider.GetRequiredService<AiObservatory.Data.Spend.FxRateProvider>();

        var rate = await fx.TryGetUsdToGbpAsync(TestContext.Current.CancellationToken);

        rate.Should().BeNull();
    }

    [Fact]
    public async Task Refresh_creates_a_plan_suggestion_with_its_evidence()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedUnusedMetaPlanAsync();
        using var client = factory.CreateAdminClient();

        var refresh = await client.PostAsync("/api/suggestions/refresh", null, ct);
        refresh.StatusCode.Should().Be(HttpStatusCode.OK);

        var suggestion = Find(await GetAsync(client), UnusedKey);
        suggestion.Should().NotBeNull();
        var found = suggestion.Value;
        found.GetProperty("ruleId").GetString().Should().Be("plan.unused");
        found.GetProperty("severity").GetString().Should().Be("attention");
        found.GetProperty("status").GetString().Should().Be("open");
        var evidence = found.GetProperty("evidence");
        evidence.GetProperty("window").GetProperty("from").GetString().Should().MatchRegex(@"^\d{4}-\d{2}-\d{2}$");
        evidence.GetProperty("figures").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Dismiss_hides_the_suggestion_and_a_refresh_does_not_bring_it_back()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedUnusedMetaPlanAsync();
        using var client = factory.CreateAdminClient();
        (await client.PostAsync("/api/suggestions/refresh", null, ct)).EnsureSuccessStatusCode();
        var id = Find(await GetAsync(client), UnusedKey)!.Value.GetProperty("id").GetGuid();

        (await client.PostAsync($"/api/suggestions/{id}/dismiss", null, ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.NoContent);
        (await client.PostAsync("/api/suggestions/refresh", null, ct)).EnsureSuccessStatusCode();

        Find(await GetAsync(client, "?status=open"), UnusedKey).Should().BeNull();
        Find(await GetAsync(client, "?status=dismissed"), UnusedKey).Should().NotBeNull();
    }

    [Fact]
    public async Task Snooze_validates_the_day_count_and_the_id()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedUnusedMetaPlanAsync();
        using var client = factory.CreateAdminClient();
        (await client.PostAsync("/api/suggestions/refresh", null, ct)).EnsureSuccessStatusCode();
        var id = Find(await GetAsync(client), UnusedKey)!.Value.GetProperty("id").GetGuid();

        (await client.PostAsync($"/api/suggestions/{id}/snooze?days=0", null, ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.BadRequest);
        (await client.PostAsync($"/api/suggestions/{id}/snooze?days=91", null, ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.BadRequest);
        (await client.PostAsync($"/api/suggestions/{Guid.NewGuid()}/snooze?days=7", null, ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.NotFound);
        (await client.PostAsync($"/api/suggestions/{Guid.NewGuid()}/dismiss", null, ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_snoozed_suggestion_leaves_the_open_list()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedUnusedMetaPlanAsync();
        using var client = factory.CreateAdminClient();
        (await client.PostAsync("/api/suggestions/refresh", null, ct)).EnsureSuccessStatusCode();
        var id = Find(await GetAsync(client, "?status=open"), UnusedKey)!.Value.GetProperty("id").GetGuid();

        (await client.PostAsync($"/api/suggestions/{id}/snooze?days=7", null, ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.NoContent);

        var open = await GetAsync(client, "?status=open");
        open.GetProperty("suggestions")
            .EnumerateArray()
            .Select(s => s.GetProperty("id").GetGuid())
            .Should()
            .NotContain(id);
    }

    [Fact]
    public async Task Concurrent_refreshes_all_succeed_and_leave_one_row_per_key()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedUnusedMetaPlanAsync();
        using var client = factory.CreateAdminClient();

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => client.PostAsync("/api/suggestions/refresh", null, ct))
        );

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        (await db.Suggestions.CountAsync(s => s.Key == UnusedKey, ct)).Should().Be(1);
    }

    [Fact]
    public async Task An_unknown_status_filter_is_a_400()
    {
        using var client = factory.CreateAdminClient();

        var response = await client.GetAsync("/api/suggestions?status=bogus", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_read_only_key_can_read_but_not_change_suggestions()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateReadOnlyClient();

        (await client.GetAsync("/api/suggestions", ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsync("/api/suggestions/refresh", null, ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.Unauthorized);
        (await client.PostAsync($"/api/suggestions/{Guid.NewGuid()}/dismiss", null, ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.Unauthorized);
    }
}
