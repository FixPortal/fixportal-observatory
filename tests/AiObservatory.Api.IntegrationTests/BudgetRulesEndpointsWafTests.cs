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

/// <summary>
/// AIO-H3: POST /api/budget-rules ThresholdGbp>0 guard. A zero/negative threshold would
/// fire a spurious alert (Insight row + email) every single evaluation cycle until deleted.
/// </summary>
[Trait("Category", "Integration")]
[Collection("ApiFactory")]
public class BudgetRulesEndpointsWafTests(AiObservatoryApiFactory factory)
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task PostBudgetRule_WhenThresholdNotPositive_ReturnsBadRequest(decimal threshold)
    {
        using var client = factory.CreateAdminClient();
        var body = new
        {
            Provider = (string?)null,
            Period = "daily",
            ThresholdGbp = threshold,
        };

        var response = await client.PostAsJsonAsync("/api/budget-rules", body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostBudgetRule_WhenThresholdPositive_ReturnsCreated()
    {
        using var client = factory.CreateAdminClient();
        var body = new
        {
            Provider = (string?)null,
            Period = "weekly",
            ThresholdGbp = 25m,
        };

        var response = await client.PostAsJsonAsync("/api/budget-rules", body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var today = NodaTime.Text.LocalDatePattern.Iso.Format(
            factory.Services.GetRequiredService<IClock>().GetCurrentInstant().InUtc().Date
        );
        created.GetProperty("evaluationStartsOn").GetString().Should().Be(today);
    }

    [Fact]
    public async Task PostBudgetRule_WhenPeriodIsAnOrdinal_ReturnsBadRequest()
    {
        using var client = factory.CreateAdminClient();

        var response = await client.PostAsJsonAsync(
            "/api/budget-rules",
            new
            {
                Provider = (string?)null,
                Period = 0,
                ThresholdGbp = 25m,
            },
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetBudgetRules_ReturnsProviderFilteredSpendForTheRulesActiveWindow()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateAdminClient();
        var createdResponse = await client.PostAsJsonAsync(
            "/api/budget-rules",
            new
            {
                Provider = "openai",
                Period = "monthly",
                ThresholdGbp = 25m,
            },
            ct
        );
        var created = await createdResponse.Content.ReadFromJsonAsync<JsonElement>(ct);
        var ruleId = created.GetProperty("id").GetGuid();
        var today = factory.Services.GetRequiredService<IClock>().GetCurrentInstant().InUtc().Date;

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            var openAi = await db.SpendVendors.SingleAsync(v => v.Provider == Provider.OpenAI, ct);
            var anthropic = await db.SpendVendors.SingleAsync(v => v.Provider == Provider.Anthropic, ct);
            var categoryId = await db.SpendCategories.Select(category => category.Id).FirstAsync(ct);
            var recordedAt = factory.Services.GetRequiredService<IClock>().GetCurrentInstant();
            db.SpendEntries.AddRange(
                Spend(openAi.Id, categoryId, today, 12.34m, recordedAt),
                Spend(anthropic.Id, categoryId, today, 99m, recordedAt),
                Spend(
                    openAi.Id,
                    categoryId,
                    new LocalDate(today.Year, today.Month, 1).PlusMonths(-1),
                    1000m,
                    recordedAt
                )
            );
            await db.SaveChangesAsync(ct);
        }

        var response = await client.GetFromJsonAsync<JsonElement>("/api/budget-rules", ct);
        var rule = response.EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == ruleId);

        rule.TryGetProperty("currentSpendGbp", out var currentSpend).Should().BeTrue();
        currentSpend.GetDecimal().Should().Be(12.34m);
        rule.GetProperty("windowStart").GetString().Should().Be(today.ToString("yyyy-MM-dd", null));
        rule.GetProperty("windowEnd").GetString().Should().Be(today.ToString("yyyy-MM-dd", null));
    }

    // NS-4: a Daily rule must show the window the alert worker actually evaluates — the last
    // COMPLETED day — not today's partial spend, which the worker won't look at until midnight.
    [Fact]
    public async Task GetBudgetRules_DailyRuleScopesToYesterdayLikeTheAlertWorker()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateAdminClient();
        var createdResponse = await client.PostAsJsonAsync(
            "/api/budget-rules",
            new
            {
                Provider = (string?)null,
                Period = "daily",
                ThresholdGbp = 25m,
            },
            ct
        );
        var created = await createdResponse.Content.ReadFromJsonAsync<JsonElement>(ct);
        var ruleId = created.GetProperty("id").GetGuid();
        var today = factory.Services.GetRequiredService<IClock>().GetCurrentInstant().InUtc().Date;
        var yesterday = today.PlusDays(-1);

        // The rule was created before yesterday, so EvaluationStartsOn does not clamp the window.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            // The rule pretends to have existed since yesterday, so EvaluationStartsOn does not
            // clamp the daily window back to today (init-only property, hence ExecuteUpdate).
            await db
                .BudgetRules.Where(r => r.Id == ruleId)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.EvaluationStartsOn, yesterday), ct);
            var categoryId = await db.SpendCategories.Select(category => category.Id).FirstAsync(ct);
            // Anthropic, not the first vendor by accident: the monthly-window test above pins an
            // OpenAI-provider rule, and this test's rows must not leak into its total.
            var vendorId = await db
                .SpendVendors.Where(v => v.Provider == Provider.Anthropic)
                .Select(v => v.Id)
                .SingleAsync(ct);
            var recordedAt = factory.Services.GetRequiredService<IClock>().GetCurrentInstant();
            db.SpendEntries.AddRange(
                Spend(vendorId, categoryId, yesterday, 12.34m, recordedAt),
                Spend(vendorId, categoryId, today, 99m, recordedAt)
            );
            await db.SaveChangesAsync(ct);
        }

        var response = await client.GetFromJsonAsync<JsonElement>("/api/budget-rules", ct);
        var ruleJson = response.EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == ruleId);

        ruleJson.GetProperty("windowStart").GetString().Should().Be(yesterday.ToString("yyyy-MM-dd", null));
        ruleJson.GetProperty("windowEnd").GetString().Should().Be(yesterday.ToString("yyyy-MM-dd", null));
        // Today's 99 is outside the evaluated window; only yesterday's completed-day spend counts.
        ruleJson.GetProperty("currentSpendGbp").GetDecimal().Should().Be(12.34m);
    }

    // A Daily rule created today has a nominal window of (yesterday, yesterday), and clamping
    // only the start to EvaluationStartsOn (today) used to return WindowStart > WindowEnd.
    [Fact]
    public async Task GetBudgetRules_DailyRuleCreatedTodayDoesNotReturnAnInvertedWindow()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateAdminClient();
        var createdResponse = await client.PostAsJsonAsync(
            "/api/budget-rules",
            new
            {
                Provider = (string?)null,
                Period = "daily",
                ThresholdGbp = 25m,
            },
            ct
        );
        var created = await createdResponse.Content.ReadFromJsonAsync<JsonElement>(ct);
        var ruleId = created.GetProperty("id").GetGuid();
        var today = factory.Services.GetRequiredService<IClock>().GetCurrentInstant().InUtc().Date;

        var response = await client.GetFromJsonAsync<JsonElement>("/api/budget-rules", ct);
        var ruleJson = response.EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == ruleId);

        ruleJson.GetProperty("windowStart").GetString().Should().Be(today.ToString("yyyy-MM-dd", null));
        ruleJson.GetProperty("windowEnd").GetString().Should().Be(today.ToString("yyyy-MM-dd", null));

        // The assembly shares one database (parallelism is off), so today's spend is whatever
        // every test in the run has filed under today — the monthly-window sibling above files
        // 99 for Anthropic. Derive the expectation from the store with the endpoint's own
        // semantics instead of asserting 0: the window pins prove the clamp, this proves the
        // reported total is the sum over that clamped window.
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        var expectedSpend = await db
            .SpendEntries.AsNoTracking()
            .Where(entry => entry.OccurredOn == today)
            .Join(
                db.SpendVendors.AsNoTracking(),
                entry => entry.VendorId,
                vendor => vendor.Id,
                (entry, vendor) => entry.AmountGbp
            )
            .SumAsync(ct);
        ruleJson.GetProperty("currentSpendGbp").GetDecimal().Should().Be(expectedSpend);
    }

    [Fact]
    public async Task DeleteBudgetRule_WhenIdDoesNotExist_ReturnsNotFound()
    {
        using var client = factory.CreateAdminClient();

        var response = await client.DeleteAsync(
            $"/api/budget-rules/{Guid.NewGuid()}",
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteBudgetRule_WhenIdExists_ReturnsNoContent()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateAdminClient();
        var createdResponse = await client.PostAsJsonAsync(
            "/api/budget-rules",
            new
            {
                Provider = (string?)null,
                Period = "weekly",
                ThresholdGbp = 25m,
            },
            ct
        );
        var ruleId = (await createdResponse.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();

        var response = await client.DeleteAsync($"/api/budget-rules/{ruleId}", ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetAsync($"/api/budget-rules/{ruleId}", ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static SpendEntry Spend(
        Guid vendorId,
        Guid categoryId,
        LocalDate occurredOn,
        decimal amountGbp,
        Instant recordedAt
    ) =>
        new()
        {
            VendorId = vendorId,
            CategoryId = categoryId,
            OccurredOn = occurredOn,
            Amount = amountGbp,
            AmountGbp = amountGbp,
            Currency = "GBP",
            FxRate = 1m,
            Source = SpendSource.Manual,
            RecordedAt = recordedAt,
            ObservedAt = recordedAt,
            CostBasis = CostBasis.Billed,
        };
}
