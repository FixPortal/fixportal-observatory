using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace AiObservatory.Api.IntegrationTests;

/// <summary>
/// AIO-H3: GET /api/aggregates malformed-date and from&gt;to validation, plus the
/// LocalDate ISO-format regression guard (the fix for a real chart-axis-scrambling bug —
/// LocalDate.ToString() with no explicit pattern used the server culture's long-date format).
/// </summary>
[Trait("Category", "Integration")]
[Collection("ApiFactory")]
public class AggregatesEndpointsWafTests(AiObservatoryApiFactory factory)
{
    [Theory]
    [InlineData("from", "not-a-date")]
    [InlineData("to", "2026-13-40")]
    public async Task GetAggregates_WhenDateMalformed_ReturnsBadRequest(string param, string value)
    {
        using var client = factory.CreateReadOnlyClient();

        var response = await client.GetAsync($"/api/aggregates?{param}={value}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetAggregates_WhenFromAfterTo_ReturnsBadRequest()
    {
        using var client = factory.CreateReadOnlyClient();

        var response = await client.GetAsync(
            "/api/aggregates?from=2026-06-15&to=2026-06-01",
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetAggregates_ReturnsDatesInIsoFormat()
    {
        // Unique out-of-range window (year 2019) so this test's own row is unambiguously
        // identifiable regardless of what other tests in the shared collection have added.
        var date = new LocalDate(2019, 5, 29);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            db.DailyAggregates.Add(
                new DailyAggregate
                {
                    Date = date,
                    Provider = Provider.Anthropic,
                    Model = "waf-iso-format-test",
                    InputTokens = 1,
                    OutputTokens = 1,
                    CostUsd = 0.01m,
                    RequestCount = 1,
                }
            );
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var client = factory.CreateReadOnlyClient();
        var response = await client.GetAsync(
            "/api/aggregates?from=2019-05-29&to=2019-05-29",
            TestContext.Current.CancellationToken
        );
        response.EnsureSuccessStatusCode();

        var rows = await response.Content.ReadFromJsonAsync<List<AggregateRow>>(TestContext.Current.CancellationToken);
        rows.Should().ContainSingle();
        // Regression guard: must be strict yyyy-MM-dd, never the server culture's long-date
        // format ("29 May 2019") that broke the frontend's slice/sort and scrambled the axis.
        rows[0].Date.Should().Be("2019-05-29");
    }

    [Fact]
    public async Task GetAggregates_WithoutBoundsReturnsTheLatestThirtyCalendarDays()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var services = scope.ServiceProvider;
            var today = services.GetRequiredService<IClock>().GetCurrentInstant().InUtc().Date;
            var db = services.GetRequiredService<AiObservatoryDbContext>();
            db.DailyAggregates.AddRange(
                Aggregate(today.PlusDays(-29), "default-window-inside"),
                Aggregate(today.PlusDays(-30), "default-window-outside")
            );
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var client = factory.CreateReadOnlyClient();
        var rows = await client.GetFromJsonAsync<List<AggregateRow>>(
            "/api/aggregates",
            TestContext.Current.CancellationToken
        );

        rows.Should().Contain(row => row.Model == "default-window-inside");
        rows.Should().NotContain(row => row.Model == "default-window-outside");
    }

    [Fact]
    public async Task GetAggregates_UsesThePinnedProviderWireName()
    {
        var date = new LocalDate(2019, 6, 1);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            db.DailyAggregates.Add(Aggregate(date, "openai-wire-name", Provider.OpenAI));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var client = factory.CreateReadOnlyClient();
        var rows = await client.GetFromJsonAsync<List<AggregateRow>>(
            "/api/aggregates?from=2019-06-01&to=2019-06-01",
            TestContext.Current.CancellationToken
        );

        rows.Should().ContainSingle().Which.Provider.Should().Be("openai");
    }

    [Fact]
    public async Task GetAggregates_ExposesProvenanceWithoutRemovingExistingFields()
    {
        var date = new LocalDate(2019, 6, 2);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            db.DailyAggregates.Add(
                new DailyAggregate
                {
                    Date = date,
                    Provider = Provider.OpenAI,
                    Model = "provenance-wire-test",
                    SourceId = UsageSourceIds.CodexLocal,
                    SourceKind = SourceKind.LocalTelemetry,
                    UsageScope = UsageScope.Subscription,
                    CostBasis = CostBasis.Notional,
                    InputTokens = 1,
                    OutputTokens = 2,
                    CacheReadTokens = 3,
                    CacheWriteTokens = 4,
                    CacheWrite1hTokens = 1,
                    CostUsd = 0.01m,
                    UnknownCostCount = 0,
                    CacheSavingsUsd = 0.007m,
                    UnknownCacheSavingsCount = 2,
                    RequestCount = 3,
                }
            );
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var client = factory.CreateReadOnlyClient();
        var rows = await client.GetFromJsonAsync<JsonElement>(
            "/api/aggregates?from=2019-06-02&to=2019-06-02",
            TestContext.Current.CancellationToken
        );
        var row = rows.EnumerateArray().Single();

        row.GetProperty("sourceId").GetString().Should().Be(UsageSourceIds.CodexLocal);
        row.GetProperty("sourceKind").GetString().Should().Be("localTelemetry");
        row.GetProperty("usageScope").GetString().Should().Be("subscription");
        row.GetProperty("costBasis").GetString().Should().Be("notional");
        row.GetProperty("inputTokens").GetInt64().Should().Be(1);
        row.GetProperty("cacheSavingsUsd").GetDecimal().Should().Be(0.007m);
        row.GetProperty("unknownCacheSavingsCount").GetInt32().Should().Be(2);
        row.GetProperty("requestCount").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task Aggregates_ExposeUnknownCostAndPatchToKnownZeroOnce()
    {
        using var client = factory.CreateAdminClient();
        var key = $"waf-unknown-cost-{Guid.NewGuid():N}";
        var body = new
        {
            Provider = "openai",
            Model = "waf-unknown-cost",
            InputTokens = 1,
            OutputTokens = 1,
            CacheReadTokens = (long?)null,
            CacheWriteTokens = (long?)null,
            CacheWrite1hTokens = (long?)null,
            ThoughtTokens = (long?)null,
            CostUsd = (decimal?)null,
            RawPayload = "{}",
            EventKey = key,
            SourceId = UsageSourceIds.LegacyApi,
            SourceKind = "legacy",
            UsageScope = "unknown",
            CostBasis = "unknown",
        };
        (await client.PostAsJsonAsync("/api/events", body, TestContext.Current.CancellationToken))
            .StatusCode.Should()
            .Be(HttpStatusCode.Created);

        var before = await client.GetFromJsonAsync<JsonElement>(
            "/api/aggregates",
            TestContext.Current.CancellationToken
        );
        var beforeRow = before.EnumerateArray().Single(e => e.GetProperty("model").GetString() == "waf-unknown-cost");
        beforeRow.GetProperty("costUsd").GetDecimal().Should().Be(0m);
        beforeRow.GetProperty("unknownCostCount").GetInt32().Should().Be(1);

        var patch = await client.PatchAsJsonAsync(
            $"/api/events/{key}/cost?provider=openai",
            new { CostUsd = 0m },
            TestContext.Current.CancellationToken
        );
        patch.StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await client.GetFromJsonAsync<JsonElement>(
            "/api/aggregates",
            TestContext.Current.CancellationToken
        );
        var afterRow = after.EnumerateArray().Single(e => e.GetProperty("model").GetString() == "waf-unknown-cost");
        afterRow.GetProperty("unknownCostCount").GetInt32().Should().Be(0);
    }

    private static DailyAggregate Aggregate(LocalDate date, string model, Provider provider = Provider.Anthropic) =>
        new()
        {
            Date = date,
            Provider = provider,
            Model = model,
            InputTokens = 1,
            OutputTokens = 1,
            CostUsd = 0.01m,
            RequestCount = 1,
        };

    private sealed record AggregateRow(string Date, string Model, string Provider);
}
