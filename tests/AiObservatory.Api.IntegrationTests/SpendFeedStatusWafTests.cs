using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiObservatory.Data;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AiObservatory.Api.IntegrationTests;

[Trait("Category", "Integration")]
public class SpendFeedStatusWafTests(AiObservatoryApiFactory factory) : IClassFixture<AiObservatoryApiFactory>
{
    private const string Route = "/api/spend/feed-status";
    private const string SourceId = "portal-spend-feed";

    private static object Body(int attempted, int posted, params (long Id, string Reason)[] skipped) =>
        new
        {
            attempted,
            posted,
            skipped = skipped.Select(s => new { expenseId = s.Id, reason = s.Reason }).ToArray(),
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
    public async Task Skips_then_clean_run_walks_the_state_through_unavailable_and_back()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = factory.CreateAdminClient();

        var skipped = await admin.PostAsJsonAsync(
            Route,
            Body(3, 1, (11, "no-vendor"), (12, "no-vendor"), (13, "no-category")),
            ct
        );
        skipped.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var row = (await RowsAsync()).Should().ContainSingle().Subject;
        row.IsConfigured.Should().BeTrue();
        row.LastAttemptAt.Should().NotBeNull();
        row.LastError.Should().Be("3 expenses skipped: 2 no-vendor, 1 no-category");

        var status = await (await admin.GetAsync("/api/sources/status", ct)).Content.ReadFromJsonAsync<JsonElement>(ct);
        status
            .EnumerateArray()
            .Single(e => e.GetProperty("sourceId").GetString() == SourceId)
            .GetProperty("status")
            .GetString()
            .Should()
            .Be("unavailable");

        // The same payload again must not add a row.
        (await admin.PostAsJsonAsync(Route, Body(3, 1, (11, "no-vendor"), (12, "no-vendor"), (13, "no-category")), ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.NoContent);
        (await RowsAsync()).Should().ContainSingle();

        (await admin.PostAsJsonAsync(Route, Body(2, 2), ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var cleared = (await RowsAsync()).Should().ContainSingle().Subject;
        cleared.LastError.Should().BeNull();
        cleared.LastSuccessAt.Should().NotBeNull();
        cleared.ConsecutiveFailureCount.Should().Be(0);

        var after = await (await admin.GetAsync("/api/sources/status", ct)).Content.ReadFromJsonAsync<JsonElement>(ct);
        after
            .EnumerateArray()
            .Single(e => e.GetProperty("sourceId").GetString() == SourceId)
            .GetProperty("status")
            .GetString()
            .Should()
            .Be("fresh");
    }

    private async Task ClearRowAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        await db
            .SourceSyncStates.Where(s => s.SourceId == SourceId)
            .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
    }

    private static object Stamped(string reportedAt, params (long Id, string Reason)[] skipped) =>
        new
        {
            attempted = skipped.Length + 1,
            posted = 1,
            reportedAt,
            skipped = skipped.Select(s => new { expenseId = s.Id, reason = s.Reason }).ToArray(),
        };

    // The row is shared across this class's tests and a future LastAttemptAt would shadow them,
    // so each reportedAt test starts and ends with the row cleared.
    [Fact]
    public async Task Older_skip_delivered_after_a_newer_clean_report_does_not_flip_the_state()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = factory.CreateAdminClient();
        await ClearRowAsync();
        try
        {
            var now = DateTimeOffset.UtcNow;
            (await admin.PostAsJsonAsync(Route, Stamped(now.AddMinutes(-1).ToString("O")), ct))
                .StatusCode.Should()
                .Be(HttpStatusCode.NoContent);

            (await admin.PostAsJsonAsync(Route, Stamped(now.AddMinutes(-10).ToString("O"), (1, "no-vendor")), ct))
                .StatusCode.Should()
                .Be(HttpStatusCode.NoContent);

            var row = (await RowsAsync()).Should().ContainSingle().Subject;
            row.IsAvailable.Should().BeTrue();
            row.LastError.Should().BeNull();
        }
        finally
        {
            await ClearRowAsync();
        }
    }

    [Fact]
    public async Task Skip_reported_at_the_same_time_counts_the_retry_and_a_newer_skip_applies()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = factory.CreateAdminClient();
        await ClearRowAsync();
        try
        {
            var at = DateTimeOffset.UtcNow.AddMinutes(-2).ToString("O");
            (await admin.PostAsJsonAsync(Route, Stamped(at, (1, "no-vendor")), ct))
                .StatusCode.Should()
                .Be(HttpStatusCode.NoContent);
            (await admin.PostAsJsonAsync(Route, Stamped(at, (1, "no-vendor")), ct))
                .StatusCode.Should()
                .Be(HttpStatusCode.NoContent);

            // The store's timestamp guard is >=, so a retry at the same reportedAt counts as a second failure.
            var afterRetry = (await RowsAsync()).Should().ContainSingle().Subject;
            afterRetry.IsAvailable.Should().BeFalse();
            afterRetry.ConsecutiveFailureCount.Should().Be(2);

            (
                await admin.PostAsJsonAsync(
                    Route,
                    Stamped(DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"), (2, "no-category")),
                    ct
                )
            )
                .StatusCode.Should()
                .Be(HttpStatusCode.NoContent);
            var afterNewerSkip = (await RowsAsync()).Should().ContainSingle().Subject;
            afterNewerSkip.IsAvailable.Should().BeFalse();
            afterNewerSkip.LastError.Should().Be("1 expense skipped: 1 no-category");
        }
        finally
        {
            await ClearRowAsync();
        }
    }

    [Fact]
    public async Task Reported_at_more_than_five_minutes_ahead_is_a_400_and_writes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await ClearRowAsync();

        var response = await factory
            .CreateAdminClient()
            .PostAsJsonAsync(Route, Stamped(DateTimeOffset.UtcNow.AddMinutes(10).ToString("O")), ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await RowsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Concurrent_posts_leave_one_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = factory.CreateAdminClient();

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => admin.PostAsJsonAsync(Route, Body(1, 0, (5, "aged-out")), ct))
        );

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.NoContent);
        (await RowsAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task Read_only_key_is_refused()
    {
        var response = await factory
            .CreateReadOnlyClient()
            .PostAsJsonAsync(Route, Body(1, 1), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Unknown_reason_is_a_400_that_does_not_echo_it()
    {
        var response = await factory
            .CreateAdminClient()
            .PostAsJsonAsync(Route, Body(2, 1, (9, "vendor-name")), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should()
            .NotContain("vendor-name");
    }

    [Fact]
    public async Task Null_skipped_entry_is_a_400_not_a_500()
    {
        using var body = new StringContent(
            """{"attempted":1,"posted":0,"skipped":[null]}""",
            System.Text.Encoding.UTF8,
            "application/json"
        );

        var response = await factory.CreateAdminClient().PostAsync(Route, body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
