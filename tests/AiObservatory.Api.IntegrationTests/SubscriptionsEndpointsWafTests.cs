using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;

namespace AiObservatory.Api.IntegrationTests;

/// <summary>
/// AIO-H3: /api/subscriptions validation branches (currency allowlist, BillingDay range,
/// negative CostAmount/extra-usage). WebApplicationFactory end-to-end.
/// </summary>
[Trait("Category", "Integration")]
[Collection("ApiFactory")]
public class SubscriptionsEndpointsWafTests(AiObservatoryApiFactory factory)
{
    private static object ValidBody(
        string provider = "anthropic",
        string? currency = "USD",
        int billingDay = 1,
        decimal costAmount = 10m
    ) =>
        new
        {
            Provider = provider,
            Name = "Test subscription",
            CostAmount = costAmount,
            Currency = currency,
            BillingDay = billingDay,
            ActiveFrom = "2026-01-01",
        };

    [Theory]
    [InlineData("EUR")]
    [InlineData("")]
    [InlineData("not-a-currency")]
    [InlineData(null)]
    public async Task PostSubscription_WhenCurrencyNotAllowed_ReturnsBadRequest(string? currency)
    {
        using var client = factory.CreateAdminClient();

        var response = await client.PostAsJsonAsync(
            "/api/subscriptions",
            ValidBody(currency: currency),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    [InlineData(-1)]
    public async Task PostSubscription_WhenBillingDayOutOfRange_ReturnsBadRequest(int billingDay)
    {
        using var client = factory.CreateAdminClient();

        var response = await client.PostAsJsonAsync(
            "/api/subscriptions",
            ValidBody(billingDay: billingDay),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostSubscription_WhenCostAmountNegative_ReturnsBadRequest()
    {
        using var client = factory.CreateAdminClient();

        var response = await client.PostAsJsonAsync(
            "/api/subscriptions",
            ValidBody(costAmount: -5m),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostSubscription_WhenUnknownProvider_ReturnsBadRequest()
    {
        using var client = factory.CreateAdminClient();

        var response = await client.PostAsJsonAsync(
            "/api/subscriptions",
            ValidBody(provider: "not-a-provider"),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostSubscription_WhenValid_ReturnsCreated()
    {
        using var client = factory.CreateAdminClient();

        var response = await client.PostAsJsonAsync(
            "/api/subscriptions",
            ValidBody(provider: "openai"),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var subscription = await response.Content.ReadFromJsonAsync<SubscriptionResponse>(
            TestContext.Current.CancellationToken
        );
        subscription!.Provider.Should().Be("openai");
    }

    [Fact]
    public async Task PostSubscription_WhenAnnual_PersistsBillingIntervalAndMonth()
    {
        using var client = factory.CreateAdminClient();

        var response = await client.PostAsJsonAsync(
            "/api/subscriptions",
            new
            {
                Provider = "google",
                Name = "Google One",
                CostAmount = 189.99m,
                Currency = "GBP",
                BillingInterval = "annual",
                BillingMonth = 7,
                BillingDay = 2,
                ActiveFrom = "2026-07-02",
            },
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var subscription = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        subscription.TryGetProperty("billingInterval", out var interval).Should().BeTrue();
        interval.GetString().Should().Be("annual");
        subscription.GetProperty("billingMonth").GetInt32().Should().Be(7);
    }

    [Fact]
    public async Task PostSubscription_WhenAnnualMonthMissing_ReturnsBadRequest()
    {
        using var client = factory.CreateAdminClient();

        var response = await client.PostAsJsonAsync(
            "/api/subscriptions",
            new
            {
                Provider = "google",
                Name = "Annual plan",
                CostAmount = 100m,
                Currency = "GBP",
                BillingInterval = "annual",
                BillingDay = 2,
                ActiveFrom = "2026-07-02",
            },
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PatchExtraUsage_WhenAmountNegative_ReturnsBadRequest()
    {
        using var client = factory.CreateAdminClient();
        var created = await client.PostAsJsonAsync(
            "/api/subscriptions",
            ValidBody(),
            TestContext.Current.CancellationToken
        );
        created.EnsureSuccessStatusCode();
        var sub = await created.Content.ReadFromJsonAsync<SubscriptionResponse>(TestContext.Current.CancellationToken);

        var response = await client.PatchAsJsonAsync(
            $"/api/subscriptions/{sub!.Id}/extra-usage",
            new { Amount = -1m },
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PatchExtraUsage_WhenAmountNonNegative_ReturnsOk()
    {
        using var client = factory.CreateAdminClient();
        var created = await client.PostAsJsonAsync(
            "/api/subscriptions",
            ValidBody(),
            TestContext.Current.CancellationToken
        );
        created.EnsureSuccessStatusCode();
        var sub = await created.Content.ReadFromJsonAsync<SubscriptionResponse>(TestContext.Current.CancellationToken);

        var response = await client.PatchAsJsonAsync(
            $"/api/subscriptions/{sub!.Id}/extra-usage",
            new { Amount = 12.5m },
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed record SubscriptionResponse(Guid Id, string Provider);
}
