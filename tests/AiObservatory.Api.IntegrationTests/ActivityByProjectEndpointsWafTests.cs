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
public sealed class ActivityByProjectEndpointsWafTests(AiObservatoryApiFactory factory)
    : IClassFixture<AiObservatoryApiFactory>
{
    [Fact]
    public async Task By_project_counts_stored_sessions_and_rounds_fractional_shares_after_an_upsert()
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        operation.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = operation.Token;
        using var client = factory.CreateAdminClient();
        var first = Session("a20-repeat", "FixPortal/a20-alpha", 0);
        using var initial = await client.PostAsJsonAsync(
            "/api/activity/sessions",
            new
            {
                Sessions = new[]
                {
                    first,
                    Session("a20-second", "FixPortal/a20-alpha", 1),
                    Session("a20-beta", "FixPortal/a20-beta", 1),
                    Session("a20-idle", "FixPortal/a20-idle", 0),
                },
            },
            ct
        );
        initial.StatusCode.Should().Be(HttpStatusCode.OK);
        using var updated = await client.PostAsJsonAsync(
            "/api/activity/sessions",
            new { Sessions = new[] { Session("a20-repeat", "FixPortal/a20-alpha", 1) } },
            ct
        );
        updated.StatusCode.Should().Be(HttpStatusCode.OK);

        using var response = await client.GetAsync("/api/activity/by-project?from=2026-07-01&to=2026-07-01", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        rows.GetArrayLength().Should().Be(3);
        var projects = rows.EnumerateArray().ToArray();
        projects
            .Select(row => row.GetProperty("project").GetString())
            .Should()
            .Equal("FixPortal/a20-alpha", "FixPortal/a20-beta", "FixPortal/a20-idle");
        AssertProject(projects[0], sessions: 2, seconds: 2, share: 66.7);
        AssertProject(projects[1], sessions: 1, seconds: 1, share: 33.3);
        AssertProject(projects[2], sessions: 1, seconds: 0, share: 0);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        var stored = await db
            .ClaudeActivitySessions.Where(s => s.StartedAt == Instant.FromUtc(2026, 7, 1, 12, 0))
            .ToListAsync(ct);
        stored.Should().HaveCount(4, "re-sending a SessionId updates its unique row instead of adding a session");
        stored.Where(s => s.SessionId == "a20-repeat").Should().ContainSingle().Which.ActiveSeconds.Should().Be(1);
    }

    [Fact]
    public async Task By_project_with_only_zero_activity_returns_zero_shares_and_real_session_counts()
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        operation.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = operation.Token;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        var start = Instant.FromUtc(2026, 7, 2, 12, 0);
        db.ClaudeActivitySessions.AddRange(
            StoredSession("a20-zero-one", "FixPortal/a20-zero", start),
            StoredSession("a20-zero-two", "FixPortal/a20-zero", start)
        );
        await db.SaveChangesAsync(ct);
        using var client = factory.CreateAdminClient();

        using var response = await client.GetAsync("/api/activity/by-project?from=2026-07-02&to=2026-07-02", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        rows.GetArrayLength().Should().Be(1);
        rows[0].GetProperty("project").GetString().Should().Be("FixPortal/a20-zero");
        AssertProject(rows[0], sessions: 2, seconds: 0, share: 0);
    }

    [Fact]
    public async Task By_project_for_a_range_without_sessions_returns_an_empty_array()
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        operation.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = operation.Token;
        using var client = factory.CreateAdminClient();
        using var response = await client.GetAsync("/api/activity/by-project?from=2026-07-03&to=2026-07-03", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        rows.ValueKind.Should().Be(JsonValueKind.Array);
        rows.GetArrayLength().Should().Be(0);
    }

    [Theory]
    [InlineData("2026-07-04", 3600)]
    [InlineData("2026-07-05", 3600)]
    public async Task By_project_prorates_a_session_across_midnight_as_daily_does(string day, long seconds)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        operation.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = operation.Token;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        if (!await db.ClaudeActivitySessions.AnyAsync(s => s.SessionId == "ar4-midnight", ct))
        {
            var start = Instant.FromUtc(2026, 7, 4, 23, 0);
            db.ClaudeActivitySessions.Add(
                new ClaudeActivitySession
                {
                    SessionId = "ar4-midnight",
                    Project = "FixPortal/ar4-midnight",
                    StartedAt = start,
                    LastSeenAt = start + Duration.FromHours(2),
                    ActiveSeconds = 7200,
                    IngestedAt = start,
                }
            );
            await db.SaveChangesAsync(ct);
        }
        using var client = factory.CreateAdminClient();

        using var byProject = await client.GetAsync($"/api/activity/by-project?from={day}&to={day}", ct);
        using var daily = await client.GetAsync($"/api/activity/daily?from={day}&to={day}", ct);

        var project = (await byProject.Content.ReadFromJsonAsync<JsonElement>(ct))
            .EnumerateArray()
            .Single(row => row.GetProperty("project").GetString() == "FixPortal/ar4-midnight");
        AssertProject(project, sessions: 1, seconds: seconds, share: 100);
        var dailyRow = (await daily.Content.ReadFromJsonAsync<JsonElement>(ct)).EnumerateArray().Single();
        dailyRow.GetProperty("activeSeconds").GetInt64().Should().Be(seconds, "both endpoints prorate the same way");
    }

    private static void AssertProject(JsonElement row, int sessions, long seconds, double share)
    {
        row.GetProperty("sessionCount").GetInt32().Should().Be(sessions);
        row.GetProperty("activeSeconds").GetInt64().Should().Be(seconds);
        row.GetProperty("sharePercent").GetDouble().Should().Be(share);
    }

    private static object Session(string id, string project, long seconds) =>
        new
        {
            SessionId = id,
            Project = project,
            StartedAtUtc = "2026-07-01T12:00:00Z",
            LastSeenAtUtc = "2026-07-01T12:00:10Z",
            ActiveSeconds = seconds,
        };

    private static ClaudeActivitySession StoredSession(string id, string project, Instant start) =>
        new()
        {
            SessionId = id,
            Project = project,
            StartedAt = start,
            LastSeenAt = start + Duration.FromSeconds(10),
            ActiveSeconds = 0,
            IngestedAt = start,
        };
}
