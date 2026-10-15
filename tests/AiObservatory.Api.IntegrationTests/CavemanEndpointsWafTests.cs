using System.Net;
using System.Net.Http.Json;
using AiObservatory.Data;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace AiObservatory.Api.IntegrationTests;

/// <summary>
/// AIO-H3: POST /api/caveman-sessions batch cap, in-batch duplicate SessionId, and
/// last-write-wins stale-skip (a replayed/stale batch must not regress a newer snapshot).
/// WebApplicationFactory end-to-end — this endpoint had zero test coverage at any level.
/// </summary>
[Trait("Category", "Integration")]
[Collection("FixedClockApiFactory")]
public class CavemanEndpointsWafTests(FixedClockApiFactory fixture)
{
    private AiObservatoryApiFactory Factory => fixture.Factory;
    private static DateTimeOffset Now => FixedClockApiFactory.Now.ToDateTimeOffset();

    private static object Session(string sessionId, DateTimeOffset occurredAtUtc, long outputTokens = 100) =>
        new
        {
            SessionId = sessionId,
            OccurredAtUtc = occurredAtUtc,
            Mode = "caveman",
            Model = "claude-sonnet-4-6",
            OutputTokens = outputTokens,
            EstSavedTokens = 10,
            EstSavedUsd = 0.001m,
        };

    [Fact]
    public async Task PostSessions_WhenBatchExceeds1000_ReturnsBadRequest()
    {
        using var client = Factory.CreateAdminClient();
        var sessions = Enumerable
            .Range(0, 1001)
            .Select(i => Session($"batch-cap-{i}-{Guid.NewGuid():N}", Now))
            .ToList();

        var response = await client.PostAsJsonAsync(
            "/api/caveman-sessions",
            new { Sessions = sessions },
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostSessions_WhenBatchHasInBatchDuplicateSessionId_ReturnsBadRequest()
    {
        using var client = Factory.CreateAdminClient();
        var sharedId = $"dup-{Guid.NewGuid():N}";
        var sessions = new[] { Session(sharedId, Now.AddMinutes(-5)), Session(sharedId, Now) };

        var response = await client.PostAsJsonAsync(
            "/api/caveman-sessions",
            new { Sessions = sessions },
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostSessions_WhenFutureOccurredAt_ReturnsBadRequest()
    {
        using var client = Factory.CreateAdminClient();
        var sessions = new[] { Session($"future-{Guid.NewGuid():N}", Now.AddHours(1)) };

        var response = await client.PostAsJsonAsync(
            "/api/caveman-sessions",
            new { Sessions = sessions },
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostSessions_WhenReplayIsOlderThanStoredSnapshot_IsSkippedAndSnapshotUnchanged()
    {
        using var client = Factory.CreateAdminClient();
        var sessionId = $"stale-{Guid.NewGuid():N}";
        // Whole-second precision: Postgres timestamptz round-trips to microseconds, so a
        // sub-microsecond Now tick component would make the post-round-trip
        // equality check below flaky.
        var newer = DateTimeOffset.FromUnixTimeSeconds(Now.AddMinutes(-1).ToUnixTimeSeconds());
        var older = newer.AddMinutes(-10);

        var first = await client.PostAsJsonAsync(
            "/api/caveman-sessions",
            new { Sessions = new[] { Session(sessionId, newer, outputTokens: 500) } },
            TestContext.Current.CancellationToken
        );
        first.EnsureSuccessStatusCode();

        // Replay an older, stale snapshot with a smaller OutputTokens for the same session.
        var replay = await client.PostAsJsonAsync(
            "/api/caveman-sessions",
            new { Sessions = new[] { Session(sessionId, older, outputTokens: 1) } },
            TestContext.Current.CancellationToken
        );
        replay.EnsureSuccessStatusCode();

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        var stored = db.CavemanSessions.Single(s => s.SessionId == sessionId);
        stored.OutputTokens.Should().Be(500, "the stale replay must not regress the newer snapshot's fields");
        stored.OccurredAt.Should().Be(Instant.FromDateTimeOffset(newer));
    }

    [Fact]
    public async Task PostSessions_WhenNewerReplayArrives_UpdatesSnapshot()
    {
        using var client = Factory.CreateAdminClient();
        var sessionId = $"fresh-{Guid.NewGuid():N}";
        var first = Now.AddMinutes(-10);
        var newer = Now.AddMinutes(-1);

        var seed = await client.PostAsJsonAsync(
            "/api/caveman-sessions",
            new { Sessions = new[] { Session(sessionId, first, outputTokens: 100) } },
            TestContext.Current.CancellationToken
        );
        seed.EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync(
            "/api/caveman-sessions",
            new { Sessions = new[] { Session(sessionId, newer, outputTokens: 999) } },
            TestContext.Current.CancellationToken
        );
        response.EnsureSuccessStatusCode();

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        var stored = db.CavemanSessions.Single(s => s.SessionId == sessionId);
        stored.OutputTokens.Should().Be(999);
    }
}
