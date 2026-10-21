using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;

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
