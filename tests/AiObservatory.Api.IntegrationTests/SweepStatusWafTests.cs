using System.Net;
using System.Net.Http.Json;
using AiObservatory.Data;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AiObservatory.Api.IntegrationTests;

[Trait("Category", "Integration")]
public class SweepStatusWafTests(AiObservatoryApiFactory factory) : IClassFixture<AiObservatoryApiFactory>
{
    private const string Route = "/api/sources/sweep-status";
    private const string SourceId = "sweep-placement@test-box";

    private static object Body(params (string Tool, string Model, int Count)[] unplaced) =>
        new
        {
            machine = "test-box",
            unplaced = unplaced.Select(u => new
            {
                tool = u.Tool,
                model = u.Model,
                count = u.Count,
            }),
        };

    private async Task<List<AiObservatory.Data.Entities.SourceSyncState>> RowsAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        return await db
            .SourceSyncStates.AsNoTracking()
            .Where(s => s.SourceId == SourceId)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Unplaced_sessions_mark_unavailable_and_a_clean_report_clears_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = factory.CreateAdminClient();

        (await client.PostAsJsonAsync(Route, Body(("codex", "mystery-1", 3), ("codex", "unknown", 1)), ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.NoContent);
        var row = (await RowsAsync()).Should().ContainSingle().Subject;
        row.IsAvailable.Should().BeFalse();
        row.LastError.Should().Be("4 sessions unplaced: codex mystery-1 (3), codex unknown (1)");

        (await client.PostAsJsonAsync(Route, Body(), ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var cleared = (await RowsAsync()).Should().ContainSingle().Subject;
        cleared.IsAvailable.Should().BeTrue();
        cleared.LastError.Should().BeNull();
    }

    [Theory]
    [InlineData("""{"machine":"Bad Box","unplaced":[]}""")]
    [InlineData("""{"machine":"test-box","unplaced":[{"tool":"codex","model":"has space","count":1}]}""")]
    [InlineData("""{"machine":"test-box","unplaced":[{"tool":"codex","model":"m","count":0}]}""")]
    [InlineData("""{"machine":"test-box","unplaced":[null]}""")]
    public async Task Malformed_reports_are_a_400(string json)
    {
        using var body = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        var response = await factory.CreateAdminClient().PostAsync(Route, body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Read_only_key_is_refused()
    {
        var response = await factory
            .CreateReadOnlyClient()
            .PostAsJsonAsync(Route, Body(), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
