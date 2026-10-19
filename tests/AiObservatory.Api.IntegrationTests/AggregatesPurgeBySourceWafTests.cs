using System.Net;
using System.Text.Json;
using AwesomeAssertions;

namespace AiObservatory.Api.IntegrationTests;

/// <summary>
/// DELETE /api/aggregates?sourceId=: clears a retired un-suffixed local namespace, and refuses
/// anything that could reach live data.
/// </summary>
[Trait("Category", "Integration")]
[Collection("ApiFactory")]
public class AggregatesPurgeBySourceWafTests(AiObservatoryApiFactory factory)
{
    [Theory]
    [InlineData("sourceId=claude-local@its-alive")]
    [InlineData("sourceId=anthropic-usage-api")]
    [InlineData("sourceId=Claude-Local")]
    [InlineData("sourceId=claude-local&provider=anthropic")]
    [InlineData("dryRun=true")]
    [InlineData("provider=anthropic&dryRun=true")]
    public async Task Refused_shapes_are_a_400(string query)
    {
        using var client = factory.CreateAdminClient();

        var response = await client.DeleteAsync($"/api/aggregates?{query}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_legacy_id_dry_run_reports_counts_and_succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateAdminClient();

        var response = await client.DeleteAsync("/api/aggregates?sourceId=never-used-local&dryRun=true", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement;
        body.GetProperty("dryRun").GetBoolean().Should().BeTrue();
        body.GetProperty("deletedEvents").GetInt32().Should().Be(0);
        body.GetProperty("deletedAggregates").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Read_only_key_cannot_purge_by_source()
    {
        using var client = factory.CreateReadOnlyClient();

        var response = await client.DeleteAsync(
            "/api/aggregates?sourceId=claude-local",
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
