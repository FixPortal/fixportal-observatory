using System.Net;
using System.Net.Http.Json;
using AiObservatory.Data;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NodaTime.Text;

namespace AiObservatory.Api.IntegrationTests;

/// <summary>
/// A sweep's attemptedAt is its attempt key, so a machine whose clock runs a little ahead keeps
/// its retries deduplicated; one far ahead is refused rather than pinning the row. Runs on the
/// fixed-clock host so "ahead" is exact.
/// </summary>
[Trait("Category", "Integration")]
[Collection("FixedClockApiFactory")]
public sealed class SweepStatusAttemptTimeWafTests(FixedClockApiFactory fixture)
{
    private const string Route = "/api/sources/sweep-status";

    private static object Report(string machine, Instant attemptedAt, int heldFiles = 0, int unplacedCount = 1) =>
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
            attemptedAt = InstantPattern.ExtendedIso.Format(attemptedAt),
        };

    private async Task<Data.Entities.SourceSyncState> RowAsync(string machine)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        return await scope
            .ServiceProvider.GetRequiredService<AiObservatoryDbContext>()
            .SourceSyncStates.AsNoTracking()
            .SingleAsync(s => s.SourceId == "sweep-placement@" + machine, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Retries_stamped_slightly_ahead_of_the_server_count_once()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = fixture.Factory.CreateAdminClient();
        var report = Report("ahead-box", FixedClockApiFactory.Now + Duration.FromMinutes(2));

        (await client.PostAsJsonAsync(Route, report, ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.PostAsJsonAsync(Route, report, ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var row = await RowAsync("ahead-box");
        row.ConsecutiveFailureCount.Should().Be(1, "both posts are one sweep's attempt");
        row.LastAttemptAt.Should().Be(FixedClockApiFactory.Now + Duration.FromMinutes(2));
    }

    [Fact]
    public async Task A_stalled_older_report_never_overwrites_a_newer_one()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = fixture.Factory.CreateAdminClient();
        var newer = FixedClockApiFactory.Now - Duration.FromHours(1);

        (await client.PostAsJsonAsync(Route, Report("stale-box", newer, unplacedCount: 0), ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.NoContent);
        (
            await client.PostAsJsonAsync(
                Route,
                Report("stale-box", newer - Duration.FromHours(1), heldFiles: 5, unplacedCount: 0),
                ct
            )
        )
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
        using var client = fixture.Factory.CreateAdminClient();
        var attemptedAt = FixedClockApiFactory.Now - Duration.FromHours(2);
        var report = Report("replay-box", attemptedAt, heldFiles: 2);

        (await client.PostAsJsonAsync(Route, report, ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.PostAsJsonAsync(Route, report, ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var row = await RowAsync("replay-box");
        row.ConsecutiveFailureCount.Should().Be(1, "a retry of one sweep is one failure");
        row.HeldFiles.Should().Be(2);
        row.LastAttemptAt.Should().Be(attemptedAt);
    }

    [Fact]
    public async Task An_attempt_far_ahead_of_the_server_clock_is_a_400()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = fixture.Factory.CreateAdminClient();

        using var response = await client.PostAsJsonAsync(
            Route,
            Report("far-ahead-box", FixedClockApiFactory.Now + Duration.FromMinutes(10)),
            ct
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(ct)).Should().Contain("clock is ahead");
    }

    [Theory]
    [InlineData("0001-01-01T00:00:00Z")]
    [InlineData("2030-01-13T12:00:00Z")]
    public async Task An_attempt_far_behind_the_server_clock_is_a_400(string attemptedAt)
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = fixture.Factory.CreateAdminClient();

        using var response = await client.PostAsJsonAsync(
            Route,
            new
            {
                machine = "far-behind-box",
                unplaced = Array.Empty<object>(),
                attemptedAt,
            },
            ct
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(ct)).Should().Contain("clock is behind");
    }
}
