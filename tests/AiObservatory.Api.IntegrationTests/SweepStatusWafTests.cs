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

    private async Task<List<Data.Entities.SourceSyncState>> RowsAsync()
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

    [Fact]
    public async Task A_report_whose_description_exceeds_the_column_is_recorded_not_a_500()
    {
        var ct = TestContext.Current.CancellationToken;
        var entries = Enumerable
            .Range(1, 5)
            .Select(i => ("antigravity", $"{new string('m', 79)}{i}", 100 - i))
            .ToArray();

        var response = await factory.CreateAdminClient().PostAsJsonAsync(Route, Body(entries), ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var row = (await RowsAsync()).Should().ContainSingle().Subject;
        row.IsAvailable.Should().BeFalse();
        row.LastError.Should().NotBeNull().And.Subject.Length.Should().BeLessThanOrEqualTo(500);
    }

    [Fact]
    public async Task Held_files_are_stored_with_the_sweep_status_warned_on_and_cleared_by_an_old_client()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = factory.CreateAdminClient();

        (
            await client.PostAsJsonAsync(
                Route,
                new
                {
                    machine = "test-box",
                    unplaced = Array.Empty<object>(),
                    heldFiles = 3,
                },
                ct
            )
        ).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var held = await StatusRowAsync(client);
        held.TryGetProperty("heldFiles", out var heldFiles).Should().BeTrue("the status row carries heldFiles");
        heldFiles.GetInt32().Should().Be(3);
        held.GetProperty("status").GetString().Should().Be("unavailable");
        held.GetProperty("lastError").GetString().Should().Be("3 files held: not posted this sweep");

        // An older client sends no heldFiles: null, and a clean report is fresh again.
        (await client.PostAsJsonAsync(Route, Body(), ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var cleared = await StatusRowAsync(client);
        cleared.GetProperty("heldFiles").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        cleared.GetProperty("status").GetString().Should().Be("fresh");
    }

    private static object Report(string machine, string attemptedAt, int heldFiles, int unplacedCount = 0) =>
        new
        {
            machine,
            unplaced = unplacedCount == 0
                ? Array.Empty<object>()
                :
                [
                    new
                    {
                        tool = "codex",
                        model = "mystery",
                        count = unplacedCount,
                    },
                ],
            heldFiles,
            attemptedAt,
        };

    private async Task<Data.Entities.SourceSyncState> RowAsync(string machine)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        return await db
            .SourceSyncStates.AsNoTracking()
            .SingleAsync(s => s.SourceId == "sweep-placement@" + machine, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_stalled_older_report_never_overwrites_a_newer_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = factory.CreateAdminClient();

        (await client.PostAsJsonAsync(Route, Report("stale-box", "2026-01-02T00:00:00Z", 0), ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.NoContent);
        (await client.PostAsJsonAsync(Route, Report("stale-box", "2026-01-01T00:00:00Z", 5), ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.NoContent);

        var row = await RowAsync("stale-box");
        row.IsAvailable.Should().BeTrue("the newer report was a success");
        row.LastError.Should().BeNull();
        row.HeldFiles.Should().Be(0);
        row.ConsecutiveFailureCount.Should().Be(0);
    }

    [Fact]
    public async Task A_retried_report_with_the_same_attempt_counts_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = factory.CreateAdminClient();
        var report = Report("replay-box", "2026-01-03T00:00:00Z", 2, unplacedCount: 1);

        (await client.PostAsJsonAsync(Route, report, ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.PostAsJsonAsync(Route, report, ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var row = await RowAsync("replay-box");
        row.ConsecutiveFailureCount.Should().Be(1, "a retry of one sweep is one failure");
        row.HeldFiles.Should().Be(2);
        row.LastAttemptAt.Should().Be(NodaTime.Instant.FromUtc(2026, 1, 3, 0, 0));
    }

    private static async Task<System.Text.Json.JsonElement> StatusRowAsync(HttpClient client)
    {
        var rows = await client.GetFromJsonAsync<System.Text.Json.JsonElement>(
            "/api/sources/status",
            TestContext.Current.CancellationToken
        );
        return rows.EnumerateArray().Single(r => r.GetProperty("sourceId").GetString() == SourceId);
    }

    [Theory]
    [InlineData("""{"machine":"test-box","unplaced":[],"heldFiles":-1}""")]
    [InlineData("""{"machine":"test-box","unplaced":[],"attemptedAt":"not-a-date"}""")]
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
