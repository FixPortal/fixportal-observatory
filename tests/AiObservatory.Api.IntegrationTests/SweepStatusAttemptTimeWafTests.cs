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

    private static object Report(string machine, Instant attemptedAt) =>
        new
        {
            machine,
            unplaced = new[]
            {
                new
                {
                    tool = "codex",
                    model = "mystery",
                    count = 1,
                },
            },
            heldFiles = 0,
            attemptedAt = InstantPattern.ExtendedIso.Format(attemptedAt),
        };

    [Fact]
    public async Task Retries_stamped_slightly_ahead_of_the_server_count_once()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = fixture.Factory.CreateAdminClient();
        var report = Report("ahead-box", FixedClockApiFactory.Now + Duration.FromMinutes(2));

        (await client.PostAsJsonAsync(Route, report, ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.PostAsJsonAsync(Route, report, ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = fixture.Factory.Services.CreateScope();
        var row = await scope
            .ServiceProvider.GetRequiredService<AiObservatoryDbContext>()
            .SourceSyncStates.AsNoTracking()
            .SingleAsync(s => s.SourceId == "sweep-placement@ahead-box", ct);
        row.ConsecutiveFailureCount.Should().Be(1, "both posts are one sweep's attempt");
        row.LastAttemptAt.Should().Be(FixedClockApiFactory.Now + Duration.FromMinutes(2));
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
}
