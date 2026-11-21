using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace AiObservatory.Api.IntegrationTests;

[Trait("Category", "Integration")]
[Collection("ApiFactory")]
public class AdversarialReviewEndpointsTests(AiObservatoryApiFactory factory)
{
    [Fact]
    public async Task GetRuns_serialises_costUnknown_for_an_unknown_cost_participant()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateAdminClient();
        var runId = $"unknown-cost-{Guid.NewGuid():N}";
        var body = new
        {
            EventType = "adversarial-review-run",
            Reviewer = "zai",
            Model = "glm",
            InputTokens = 1,
            OutputTokens = 1,
            CostUsd = 0m,
            ReviewDurationMs = 1,
            IssuesRaised = 2,
            IssuesAccepted = 1,
            RunId = runId,
            Role = "reviewer",
            Repo = (string?)null,
            CostUnknown = true,
        };

        var created = await client.PostAsJsonAsync("/api/adversarial-review/runs", body, ct);
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var runs = await client.GetFromJsonAsync<JsonElement>($"/api/adversarial-review/runs?runId={runId}", ct);

        runs.GetArrayLength().Should().Be(1);
        runs[0].GetProperty("costUnknown").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("?since=2021-01-01", false)]
    [InlineData("?since=2020-01-01", true)]
    public async Task GetRuns_returns_every_row_unless_since_bounds_it_by_recorded_date(string query, bool included)
    {
        var ct = TestContext.Current.CancellationToken;
        var runId = $"since-window-{Guid.NewGuid():N}";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            db.AdversarialReviewRuns.Add(
                new AdversarialReviewRun
                {
                    Reviewer = "zai",
                    Model = "glm",
                    RunId = runId,
                    RecordedAt = Instant.FromUtc(2020, 1, 1, 0, 0),
                }
            );
            await db.SaveChangesAsync(ct);
        }
        using var client = factory.CreateAdminClient();

        var runs = await client.GetFromJsonAsync<JsonElement>($"/api/adversarial-review/runs{query}", ct);

        runs.EnumerateArray().Any(r => r.GetProperty("runId").GetString() == runId).Should().Be(included);
    }

    [Fact]
    public async Task Unknown_cost_with_a_positive_amount_is_stored_as_zero()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateAdminClient();
        var runId = $"unknown-positive-{Guid.NewGuid():N}";
        var body = new
        {
            EventType = "adversarial-review-run",
            Reviewer = "zai",
            Model = "glm",
            InputTokens = 1,
            OutputTokens = 1,
            CostUsd = 2.5m,
            ReviewDurationMs = 1,
            IssuesRaised = 2,
            IssuesAccepted = 1,
            RunId = runId,
            Role = "reviewer",
            Repo = (string?)null,
            CostUnknown = true,
        };

        (await client.PostAsJsonAsync("/api/adversarial-review/runs", body, ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.Created);

        var runs = await client.GetFromJsonAsync<JsonElement>($"/api/adversarial-review/runs?runId={runId}", ct);

        runs[0].GetProperty("costUnknown").GetBoolean().Should().BeTrue();
        runs[0].GetProperty("costUsd").GetDecimal().Should().Be(0m);
        runs[0].GetProperty("costPerAcceptedFinding").ValueKind.Should().Be(JsonValueKind.Null);
    }
}
