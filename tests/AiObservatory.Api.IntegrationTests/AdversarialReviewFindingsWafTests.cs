using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;

namespace AiObservatory.Api.IntegrationTests;

[Trait("Category", "Integration")]
[Collection("ApiFactory")]
public class AdversarialReviewFindingsWafTests(AiObservatoryApiFactory factory)
{
    private const string FindingsRoute = "/api/adversarial-review/findings";

    private static string FixtureDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "superpowers", "contracts", "panel-findings");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("panel-findings contract fixtures not found above the test binaries");
    }

    private static JsonObject ValidBody(string runId, string repo = "example-app")
    {
        var body = JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureDir(), "post-valid.json")))!.AsObject();
        body["runId"] = runId;
        body["repo"] = repo;
        return body;
    }

    public static TheoryData<string> InvalidCaseNames()
    {
        var data = new TheoryData<string>();
        var cases = JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureDir(), "post-invalid-cases.json")))!.AsArray();
        foreach (var c in cases.Where(c => c!["body"] is not null))
        {
            data.Add(c!["name"]!.GetValue<string>());
        }
        return data;
    }

    private async Task SeedRunAsync(HttpClient client, string runId, string repo = "example-app")
    {
        var body = new
        {
            EventType = "adversarial-review-run",
            Reviewer = "zai",
            Model = "glm-5.3",
            InputTokens = 1,
            OutputTokens = 1,
            CostUsd = 1m,
            ReviewDurationMs = 1,
            IssuesRaised = 2,
            IssuesAccepted = 1,
            RunId = runId,
            Role = "reviewer",
            Repo = repo,
        };
        (await client.PostAsJsonAsync("/api/adversarial-review/runs", body, TestContext.Current.CancellationToken))
            .IsSuccessStatusCode.Should()
            .BeTrue();
    }

    [Fact]
    public async Task Posted_findings_come_back_with_their_raisers_and_assessors()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateAdminClient();
        var runId = $"findings-{Guid.NewGuid():N}";
        await SeedRunAsync(client, runId);

        var post = await client.PostAsJsonAsync(FindingsRoute, ValidBody(runId), ct);
        post.StatusCode.Should().Be(HttpStatusCode.OK);

        var rows = await client.GetFromJsonAsync<JsonElement>($"{FindingsRoute}?runId={runId}", ct);

        rows.GetArrayLength().Should().Be(4);
        var corroborated = rows.EnumerateArray()
            .Single(r =>
                r.GetProperty("chunkId").GetString() == "C1" && r.GetProperty("findingId").GetString() == "F2"
            );
        corroborated.GetProperty("raisedBy").GetArrayLength().Should().Be(2);
        corroborated.GetProperty("assessments")[0].GetProperty("verdict").GetString().Should().Be("agree");
        corroborated.GetProperty("repo").GetString().Should().Be("example-app");
    }

    [Fact]
    public async Task A_repost_replaces_the_set_and_leaves_no_stale_findings()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateAdminClient();
        var runId = $"replace-{Guid.NewGuid():N}";
        await SeedRunAsync(client, runId);
        (await client.PostAsJsonAsync(FindingsRoute, ValidBody(runId), ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var smaller = ValidBody(runId);
        smaller["findings"] = new JsonArray(smaller["findings"]!.AsArray()[0]!.DeepClone());
        (await client.PostAsJsonAsync(FindingsRoute, smaller, ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync(FindingsRoute, smaller, ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var rows = await client.GetFromJsonAsync<JsonElement>($"{FindingsRoute}?runId={runId}", ct);
        rows.GetArrayLength().Should().Be(1);
        rows[0].GetProperty("assessments").GetArrayLength().Should().Be(2);
    }

    [Theory]
    [MemberData(nameof(InvalidCaseNames))]
    public async Task Invalid_bodies_are_a_400_that_never_echoes_input(string name)
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateAdminClient();
        var runId = $"invalid-{Guid.NewGuid():N}";
        await SeedRunAsync(client, runId, "x");
        var cases = JsonNode
            .Parse(await File.ReadAllTextAsync(Path.Combine(FixtureDir(), "post-invalid-cases.json"), ct))!
            .AsArray();
        var body = cases.Single(c => c!["name"]!.GetValue<string>() == name)!["body"]!.DeepClone().AsObject();
        body["runId"] = runId;

        var response = await client.PostAsJsonAsync(FindingsRoute, body, ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(ct)).Should().NotContain("drop").And.NotContain("Open AI");
    }

    [Fact]
    public async Task A_model_over_200_characters_is_a_400()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateAdminClient();
        var runId = $"longmodel-{Guid.NewGuid():N}";
        await SeedRunAsync(client, runId);
        var body = ValidBody(runId);
        body["findings"]![0]!["raisedBy"]![0]!["model"] = new string('m', 201);

        (await client.PostAsJsonAsync(FindingsRoute, body, ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task More_than_2000_findings_is_a_400()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateAdminClient();
        var runId = $"toomany-{Guid.NewGuid():N}";
        await SeedRunAsync(client, runId);
        var findings = new JsonArray();
        for (var i = 0; i < 2001; i++)
        {
            findings.Add(
                JsonNode.Parse(
                    $$"""{"chunkId":"single","findingId":"F{{i}}","severity":"low","outcome":"dropped","raisedBy":[{"reviewer":"zai","model":"m"}],"assessments":[]}"""
                )
            );
        }
        var body = new JsonObject
        {
            ["runId"] = runId,
            ["repo"] = "example-app",
            ["findings"] = findings,
        };

        (await client.PostAsJsonAsync(FindingsRoute, body, ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_run_with_no_run_rows_is_a_404()
    {
        using var client = factory.CreateAdminClient();

        var response = await client.PostAsJsonAsync(
            FindingsRoute,
            ValidBody($"norun-{Guid.NewGuid():N}"),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_repo_that_differs_from_the_run_rows_is_a_400()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateAdminClient();
        var runId = $"repo-{Guid.NewGuid():N}";
        await SeedRunAsync(client, runId, "example-app");

        var response = await client.PostAsJsonAsync(FindingsRoute, ValidBody(runId, "another-app"), ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Deleting_the_run_removes_its_findings()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateAdminClient();
        var runId = $"delete-{Guid.NewGuid():N}";
        await SeedRunAsync(client, runId);
        (await client.PostAsJsonAsync(FindingsRoute, ValidBody(runId), ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.DeleteAsync($"/api/adversarial-review/runs/{runId}", ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.OK);

        var rows = await client.GetFromJsonAsync<JsonElement>($"{FindingsRoute}?runId={runId}", ct);
        rows.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Read_only_key_cannot_post_findings()
    {
        using var body = new StringContent("{}", Encoding.UTF8, "application/json");

        var response = await factory
            .CreateReadOnlyClient()
            .PostAsync(FindingsRoute, body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
