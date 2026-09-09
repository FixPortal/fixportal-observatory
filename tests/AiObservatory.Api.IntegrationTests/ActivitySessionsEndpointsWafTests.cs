using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiObservatory.Data;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AiObservatory.Api.IntegrationTests;

/// <summary>
/// POST /api/activity/sessions — the producers are out-of-repo PowerShell hooks, so this
/// contract had no in-repo coverage: the per-row validation messages, the 1000-row cap, the
/// monotonic merge (a re-sent older snapshot must not regress the stored row), and the 409 on
/// a genuinely concurrent first write of the same SessionId.
/// </summary>
[Trait("Category", "Integration")]
[Collection("ApiFactory")]
public class ActivitySessionsEndpointsWafTests(AiObservatoryApiFactory factory)
{
    private static object Session(
        string sessionId,
        string project = "FixPortal/example",
        DateTimeOffset? startedAtUtc = null,
        DateTimeOffset? lastSeenAtUtc = null,
        long activeSeconds = 60
    ) =>
        new
        {
            SessionId = sessionId,
            Project = project,
            StartedAtUtc = startedAtUtc ?? DateTimeOffset.UtcNow.AddHours(-2),
            LastSeenAtUtc = lastSeenAtUtc ?? DateTimeOffset.UtcNow.AddHours(-1),
            ActiveSeconds = activeSeconds,
        };

    private static async Task<long> UpsertedAsync(HttpResponseMessage response, CancellationToken ct) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("upserted").GetInt64();

    [Fact]
    public async Task PostActivitySessions_WithAnEmptyBatch_ReturnsZeroUpserted()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateAdminClient();

        var response = await client.PostAsJsonAsync(
            "/api/activity/sessions",
            new { Sessions = Array.Empty<object>() },
            ct
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await UpsertedAsync(response, ct)).Should().Be(0);
    }

    [Fact]
    public async Task PostActivitySessions_EnforcesThe1000RowCap()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateAdminClient();
        var batchId = Guid.NewGuid().ToString("N");
        var overCap = Enumerable.Range(0, 1001).Select(index => Session($"cap-{batchId}-{index}")).ToArray();

        var rejected = await client.PostAsJsonAsync("/api/activity/sessions", new { Sessions = overCap }, ct);

        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await rejected.Content.ReadAsStringAsync(ct))
            .Should()
            .Contain("Cannot upsert more than 1000 sessions at once.");

        // The boundary itself is accepted.
        var atCap = Enumerable.Range(0, 1000).Select(index => Session($"cap-ok-{batchId}-{index}")).ToArray();
        var accepted = await client.PostAsJsonAsync("/api/activity/sessions", new { Sessions = atCap }, ct);
        accepted.StatusCode.Should().Be(HttpStatusCode.OK);
        (await UpsertedAsync(accepted, ct)).Should().Be(1000);
    }

    [Theory]
    // Each case mutates one field of an otherwise-valid row; the endpoint returns the first
    // validation failure it hits, with a message naming the problem.
    [InlineData("negative-active", "ActiveSeconds must be non-negative.")]
    [InlineData("seen-before-start", "LastSeenAtUtc must not be before StartedAtUtc")]
    [InlineData("future", "Timestamps must not be in the future")]
    [InlineData("active-exceeds-elapsed", "ActiveSeconds exceeds elapsed time")]
    [InlineData("blank-session-id", "SessionId invalid")]
    [InlineData("blank-project", "Project invalid")]
    public async Task PostActivitySessions_RejectsAnInvalidRowWithASpecificMessage(string scenario, string message)
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateAdminClient();
        var sessionId = $"invalid-{scenario}-{Guid.NewGuid():N}";
        var session = scenario switch
        {
            "negative-active" => Session(sessionId, activeSeconds: -1),
            "seen-before-start" => Session(
                sessionId,
                startedAtUtc: DateTimeOffset.UtcNow.AddHours(-1),
                lastSeenAtUtc: DateTimeOffset.UtcNow.AddHours(-2)
            ),
            "future" => Session(sessionId, lastSeenAtUtc: DateTimeOffset.UtcNow.AddHours(1)),
            "active-exceeds-elapsed" => Session(
                sessionId,
                startedAtUtc: DateTimeOffset.UtcNow.AddHours(-1),
                lastSeenAtUtc: DateTimeOffset.UtcNow,
                activeSeconds: 3601
            ),
            "blank-session-id" => Session(""),
            "blank-project" => Session(sessionId, project: ""),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        var response = await client.PostAsJsonAsync("/api/activity/sessions", new { Sessions = new[] { session } }, ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(ct)).Should().Contain(message);
    }

    [Fact]
    public async Task PostActivitySessions_RejectsADuplicateSessionIdWithinTheBatch()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateAdminClient();
        var sessionId = $"dup-{Guid.NewGuid():N}";

        var response = await client.PostAsJsonAsync(
            "/api/activity/sessions",
            new { Sessions = new[] { Session(sessionId), Session(sessionId) } },
            ct
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(ct)).Should().Contain($"Duplicate SessionId in batch: '{sessionId}'");
    }

    [Fact]
    public async Task PostActivitySessions_MergesMonotonicallyAndNeverRegressesAStoredRow()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateAdminClient();
        var sessionId = $"merge-{Guid.NewGuid():N}";
        var started = WholeMicroseconds(DateTimeOffset.UtcNow.AddHours(-4));
        var t1 = WholeMicroseconds(DateTimeOffset.UtcNow.AddHours(-3));
        var t2 = WholeMicroseconds(DateTimeOffset.UtcNow.AddHours(-2));
        var t3 = WholeMicroseconds(DateTimeOffset.UtcNow.AddHours(-1));

        var created = await client.PostAsJsonAsync(
            "/api/activity/sessions",
            new
            {
                Sessions = new[] { Session(sessionId, startedAtUtc: started, lastSeenAtUtc: t2, activeSeconds: 100) },
            },
            ct
        );
        (await UpsertedAsync(created, ct)).Should().Be(1);

        // A fully older snapshot is a no-op and says so: neither field can regress.
        var regressed = await client.PostAsJsonAsync(
            "/api/activity/sessions",
            new
            {
                Sessions = new[] { Session(sessionId, startedAtUtc: started, lastSeenAtUtc: t1, activeSeconds: 50) },
            },
            ct
        );
        (await UpsertedAsync(regressed, ct)).Should().Be(0);

        // A higher ActiveSeconds with an OLDER LastSeenAt still applies the seconds...
        var higherSeconds = await client.PostAsJsonAsync(
            "/api/activity/sessions",
            new
            {
                Sessions = new[] { Session(sessionId, startedAtUtc: started, lastSeenAtUtc: t1, activeSeconds: 150) },
            },
            ct
        );
        (await UpsertedAsync(higherSeconds, ct)).Should().Be(1);

        // ...and a newer LastSeenAt with LOWER seconds still advances the timestamp.
        var newerSeen = await client.PostAsJsonAsync(
            "/api/activity/sessions",
            new
            {
                Sessions = new[] { Session(sessionId, startedAtUtc: started, lastSeenAtUtc: t3, activeSeconds: 120) },
            },
            ct
        );
        (await UpsertedAsync(newerSeen, ct)).Should().Be(1);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        var row = await db.ClaudeActivitySessions.AsNoTracking().SingleAsync(s => s.SessionId == sessionId, ct);
        row.ActiveSeconds.Should().Be(150, "each field takes the maximum ever seen");
        row.LastSeenAt.ToDateTimeOffset().Should().Be(t3);
    }

    [Fact]
    public async Task PostActivitySessions_ConcurrentFirstWriteOfTheSameSession_OneWinsOneConflicts()
    {
        var ct = TestContext.Current.CancellationToken;
        var sessionId = $"race-{Guid.NewGuid():N}";
        using var firstClient = factory.CreateAdminClient();
        using var secondClient = factory.CreateAdminClient();

        // Force the race the endpoint's 409 arm exists for: a SHARE table lock lets both
        // requests' existence SELECTs through (both see no row and stage an insert) while
        // blocking both INSERTs, so both are parked past the existence check before either can
        // write. Releasing the lock serializes them: one commits, the other unique-violates.
        HttpResponseMessage[] responses;
        await using (var lockScope = factory.Services.CreateAsyncScope())
        {
            var lockDb = lockScope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            await using var tableLock = await lockDb.Database.BeginTransactionAsync(ct);
            await lockDb.Database.ExecuteSqlRawAsync("""LOCK TABLE "ClaudeActivitySessions" IN SHARE MODE""", ct);

            var firstTask = firstClient.PostAsJsonAsync(
                "/api/activity/sessions",
                new { Sessions = new[] { Session(sessionId) } },
                ct
            );
            var secondTask = secondClient.PostAsJsonAsync(
                "/api/activity/sessions",
                new { Sessions = new[] { Session(sessionId) } },
                ct
            );

            await WaitForBothInsertsBlockedAsync(ct);
            await tableLock.RollbackAsync(ct);

            responses = await Task.WhenAll(firstTask, secondTask);
        }

        responses
            .Select(response => response.StatusCode)
            .Should()
            .BeEquivalentTo([HttpStatusCode.OK, HttpStatusCode.Conflict]);
        var winner = responses.Should().ContainSingle(response => response.StatusCode == HttpStatusCode.OK).Which;
        (await UpsertedAsync(winner, ct)).Should().Be(1);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        var rows = await db.ClaudeActivitySessions.AsNoTracking().Where(s => s.SessionId == sessionId).ToListAsync(ct);
        rows.Should().ContainSingle("the monotonic merge converges on one row; the loser is told to retry");
    }

    /// <summary>
    /// Waits until BOTH racing requests are parked in a PostgreSQL lock wait on their INSERT —
    /// the deterministic proof that both passed the existence check before either could write.
    /// </summary>
    private async Task WaitForBothInsertsBlockedAsync(CancellationToken ct)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var blocked = await db
                .Database.SqlQueryRaw<int>(
                    """
                    SELECT COUNT(*)::int AS "Value"
                    FROM pg_stat_activity
                    WHERE datname = current_database()
                        AND pid <> pg_backend_pid()
                        AND wait_event_type = 'Lock'
                        AND query LIKE '%INSERT INTO "ClaudeActivitySessions"%'
                    """
                )
                .SingleAsync(ct);
            if (blocked >= 2)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
        }

        throw new TimeoutException("Both racing session upserts did not reach the expected INSERT lock wait.");
    }

    // PostgreSQL timestamps are microsecond-precision; trim the sub-microsecond ticks off
    // expected values so a round-tripped row compares exactly (10 ticks = 1 microsecond).
    private static DateTimeOffset WholeMicroseconds(DateTimeOffset value) => value.AddTicks(-(value.Ticks % 10));
}
