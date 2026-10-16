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
    public async Task Concurrent_new_batches_report_one_conflict_and_do_not_persist_the_losing_companion()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        var commonId = $"c8-common-{Guid.NewGuid():N}";
        var firstOnly = $"c8-first-{Guid.NewGuid():N}";
        var secondOnly = $"c8-second-{Guid.NewGuid():N}";
        using var firstClient = Factory.CreateAdminClient();
        using var secondClient = Factory.CreateAdminClient();
        await using var lockScope = Factory.Services.CreateAsyncScope();
        var lockDb = lockScope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        var tableLock = await lockDb.Database.BeginTransactionAsync(ct);
        Task<HttpResponseMessage>? first = null;
        Task<HttpResponseMessage>? second = null;
        try
        {
            var blocker = await lockDb
                .Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"")
                .SingleAsync(ct);
            await lockDb.Database.ExecuteSqlRawAsync("""LOCK TABLE "CavemanSessions" IN SHARE MODE""", ct);
            first = firstClient.PostAsJsonAsync(
                "/api/caveman-sessions",
                new
                {
                    Sessions = new[]
                    {
                        Session(commonId, Now.AddMinutes(-1), 111),
                        Session(firstOnly, Now.AddMinutes(-1), 11),
                    },
                },
                ct
            );
            second = secondClient.PostAsJsonAsync(
                "/api/caveman-sessions",
                new
                {
                    Sessions = new[]
                    {
                        Session(commonId, Now.AddMinutes(-1), 222),
                        Session(secondOnly, Now.AddMinutes(-1), 22),
                    },
                },
                ct
            );
            await WaitForBlockedInsertsAsync(blocker, 2, ct);
            await tableLock.RollbackAsync(ct);
            var responses = await Task.WhenAll(first, second);
            responses.Select(r => r.StatusCode).Should().BeEquivalentTo([HttpStatusCode.OK, HttpStatusCode.Conflict]);
            var firstWon = responses[0].StatusCode == HttpStatusCode.OK;
            var winner = responses[firstWon ? 0 : 1];
            var loser = responses[firstWon ? 1 : 0];
            (await UpsertedAsync(winner, ct)).Should().Be(2);
            (await loser.Content.ReadAsStringAsync(ct))
                .Should()
                .Contain("Concurrent write to one or more sessions; retry.");
            var winnerOnly = firstWon ? firstOnly : secondOnly;
            var loserOnly = firstWon ? secondOnly : firstOnly;
            await using var readScope = Factory.Services.CreateAsyncScope();
            var read = readScope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            var ownIds = new[] { commonId, firstOnly, secondOnly };
            var rows = await read
                .CavemanSessions.AsNoTracking()
                .Where(row => ownIds.Contains(row.SessionId))
                .ToListAsync(ct);
            rows.Select(row => row.SessionId).Should().BeEquivalentTo(commonId, winnerOnly);
            rows.Should().NotContain(row => row.SessionId == loserOnly);
            rows.Single(row => row.SessionId == commonId).OutputTokens.Should().Be(firstWon ? 111 : 222);
        }
        finally
        {
            await tableLock.DisposeAsync();
            await CancelAndDrainAsync(deadline, first, second);
        }
    }

    [Fact]
    public async Task A_conflicting_batch_rolls_back_its_existing_update_and_new_companion_then_retries()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        var existingId = $"c8-existing-{Guid.NewGuid():N}";
        var commonId = $"c8-contended-{Guid.NewGuid():N}";
        var companionId = $"c8-companion-{Guid.NewGuid():N}";
        using var client = Factory.CreateAdminClient();
        using var seed = await client.PostAsJsonAsync(
            "/api/caveman-sessions",
            new { Sessions = new[] { Session(existingId, Now.AddMinutes(-3), 17) } },
            ct
        );
        seed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await UpsertedAsync(seed, ct)).Should().Be(1);
        await using var readScope = Factory.Services.CreateAsyncScope();
        var read = readScope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        var before = await read.CavemanSessions.AsNoTracking().SingleAsync(row => row.SessionId == existingId, ct);
        await using var winnerScope = Factory.Services.CreateAsyncScope();
        var winnerDb = winnerScope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        var transaction = await winnerDb.Database.BeginTransactionAsync(ct);
        Task<HttpResponseMessage>? pending = null;
        var batch = new
        {
            Sessions = new[]
            {
                Session(existingId, Now.AddMinutes(-1), 333),
                Session(companionId, Now.AddMinutes(-1), 44),
                Session(commonId, Now.AddMinutes(-1), 444),
            },
        };
        try
        {
            var blocker = await winnerDb
                .Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"")
                .SingleAsync(ct);
            var winner = new CavemanSession
            {
                SessionId = commonId,
                OccurredAt = Instant.FromDateTimeOffset(Now.AddMinutes(-2)),
                Mode = "winner",
                Model = "winner-model",
                OutputTokens = 9,
                EstSavedTokens = 1,
                EstSavedUsd = 0.01m,
            };
            winnerDb.CavemanSessions.Add(winner);
            await winnerDb.SaveChangesAsync(ct);
            (await read.CavemanSessions.AnyAsync(row => row.SessionId == commonId, ct))
                .Should()
                .BeFalse("the winner remains uncommitted");
            pending = client.PostAsJsonAsync("/api/caveman-sessions", batch, ct);
            await WaitForBlockedInsertsAsync(blocker, 1, ct);
            await transaction.CommitAsync(ct);
            var conflict = await pending;
            conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await conflict.Content.ReadAsStringAsync(ct))
                .Should()
                .Contain("Concurrent write to one or more sessions; retry.");
            (await read.CavemanSessions.AsNoTracking().SingleAsync(row => row.SessionId == existingId, ct))
                .Should()
                .BeEquivalentTo(before, "the ExecuteUpdate earlier in the rejected batch must also roll back");
            (await read.CavemanSessions.AnyAsync(row => row.SessionId == companionId, ct)).Should().BeFalse();
            (await read.CavemanSessions.AsNoTracking().SingleAsync(row => row.SessionId == commonId, ct))
                .Should()
                .BeEquivalentTo(winner);

            using var retry = await client.PostAsJsonAsync("/api/caveman-sessions", batch, ct);
            retry.StatusCode.Should().Be(HttpStatusCode.OK);
            (await UpsertedAsync(retry, ct)).Should().Be(3);
            var ownIds = new[] { existingId, commonId, companionId };
            var rows = await read
                .CavemanSessions.AsNoTracking()
                .Where(row => ownIds.Contains(row.SessionId))
                .ToListAsync(ct);
            rows.Should().HaveCount(3);
            rows.Single(row => row.SessionId == existingId).OutputTokens.Should().Be(333);
            rows.Single(row => row.SessionId == commonId).OutputTokens.Should().Be(444);
            rows.Single(row => row.SessionId == companionId).OutputTokens.Should().Be(44);
        }
        finally
        {
            await transaction.DisposeAsync();
            await CancelAndDrainAsync(deadline, pending);
        }
    }

    private async Task WaitForBlockedInsertsAsync(int blockerPid, int expected, CancellationToken ct)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        const string insert = "INSERT INTO \"CavemanSessions\"";
        while (true)
        {
            var blocked = await db
                .Database.SqlQuery<int>(
                    $"""
                    SELECT a.pid AS "Value" FROM pg_stat_activity a
                    WHERE a.datname = current_database() AND a.wait_event_type = 'Lock'
                        AND a.query LIKE '%' || {insert} || '%'
                        AND {blockerPid} = ANY(pg_blocking_pids(a.pid))
                        AND EXISTS (SELECT 1 FROM pg_locks l WHERE l.pid = a.pid AND NOT l.granted)
                    """
                )
                .ToListAsync(ct);
            if (blocked.Count == expected)
            {
                Console.WriteLine(
                    $"Observed Caveman INSERT waiters {string.Join(",", blocked)} blocked by {blockerPid} in {db.Database.GetDbConnection().Database}."
                );
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
        }
    }

    private static async Task<int> UpsertedAsync(HttpResponseMessage response, CancellationToken ct) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct))
            .GetProperty("upserted")
            .GetInt32();

    private static async Task CancelAndDrainAsync(
        CancellationTokenSource deadline,
        params Task<HttpResponseMessage>?[] requests
    )
    {
        await deadline.CancelAsync();
        foreach (var request in requests)
        {
            if (request is null)
            {
                continue;
            }
            try
            {
                (await request.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)).Dispose();
            }
            catch (Exception exception) when (deadline.IsCancellationRequested)
            {
                Console.WriteLine($"Cancelled HTTP cleanup: {exception.GetType().Name}");
            }
        }
    }

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
        var stored = await db.CavemanSessions.SingleAsync(
            s => s.SessionId == sessionId,
            TestContext.Current.CancellationToken
        );
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
        var stored = await db.CavemanSessions.SingleAsync(
            s => s.SessionId == sessionId,
            TestContext.Current.CancellationToken
        );
        stored.OutputTokens.Should().Be(999);
    }
}
