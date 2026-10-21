using AiObservatory.Api.Endpoints;
using AwesomeAssertions;
using NodaTime;

namespace AiObservatory.Api.Tests;

public class ActivityEndpointsTests
{
    [Fact]
    public void MergeIntervalSeconds_WhenEmpty_ReturnsZero()
    {
        ActivityEndpoints.MergeIntervalSeconds([]).Should().Be(0);
    }

    [Fact]
    public void MergeIntervalSeconds_WhenSingleSpan_ReturnsItsDuration()
    {
        var span = (Instant.FromUtc(2026, 7, 1, 9, 0), Instant.FromUtc(2026, 7, 1, 10, 0));
        ActivityEndpoints.MergeIntervalSeconds([span]).Should().Be(3600);
    }

    [Fact]
    public void MergeIntervalSeconds_WhenSpansDisjoint_SumsBoth()
    {
        var a = (Instant.FromUtc(2026, 7, 1, 9, 0), Instant.FromUtc(2026, 7, 1, 10, 0));
        var b = (Instant.FromUtc(2026, 7, 1, 11, 0), Instant.FromUtc(2026, 7, 1, 12, 0));
        ActivityEndpoints.MergeIntervalSeconds([a, b]).Should().Be(7200);
    }

    [Fact]
    public void MergeIntervalSeconds_WhenSpansOverlap_CountsUnionNotSum()
    {
        // Two parallel sessions covering the same hour must not double-count —
        // this is the fix for the >24h/day bar chart bug.
        var a = (Instant.FromUtc(2026, 7, 1, 9, 0), Instant.FromUtc(2026, 7, 1, 11, 0));
        var b = (Instant.FromUtc(2026, 7, 1, 10, 0), Instant.FromUtc(2026, 7, 1, 12, 0));
        ActivityEndpoints.MergeIntervalSeconds([a, b]).Should().Be(3 * 3600);
    }

    [Fact]
    public void MergeIntervalSeconds_WhenOneSpanFullyContainsAnother_CountsOuterOnly()
    {
        var outer = (Instant.FromUtc(2026, 7, 1, 9, 0), Instant.FromUtc(2026, 7, 1, 12, 0));
        var inner = (Instant.FromUtc(2026, 7, 1, 10, 0), Instant.FromUtc(2026, 7, 1, 11, 0));
        ActivityEndpoints.MergeIntervalSeconds([outer, inner]).Should().Be(3 * 3600);
    }

    [Fact]
    public void MergeIntervalSeconds_WhenSpansTouchExactly_MergesAdjacent()
    {
        var a = (Instant.FromUtc(2026, 7, 1, 9, 0), Instant.FromUtc(2026, 7, 1, 10, 0));
        var b = (Instant.FromUtc(2026, 7, 1, 10, 0), Instant.FromUtc(2026, 7, 1, 11, 0));
        ActivityEndpoints.MergeIntervalSeconds([a, b]).Should().Be(2 * 3600);
    }

    [Fact]
    public void MergeIntervalSeconds_WhenUnordered_StillMergesCorrectly()
    {
        var late = (Instant.FromUtc(2026, 7, 1, 11, 0), Instant.FromUtc(2026, 7, 1, 12, 0));
        var early = (Instant.FromUtc(2026, 7, 1, 9, 0), Instant.FromUtc(2026, 7, 1, 10, 0));
        ActivityEndpoints.MergeIntervalSeconds([late, early]).Should().Be(7200);
    }

    [Fact]
    public void MergeIntervalSeconds_WhenSpanIsZeroLengthOrInverted_IsIgnored()
    {
        var valid = (Instant.FromUtc(2026, 7, 1, 9, 0), Instant.FromUtc(2026, 7, 1, 10, 0));
        var zeroLength = (Instant.FromUtc(2026, 7, 1, 12, 0), Instant.FromUtc(2026, 7, 1, 12, 0));
        var inverted = (Instant.FromUtc(2026, 7, 1, 14, 0), Instant.FromUtc(2026, 7, 1, 13, 0));
        ActivityEndpoints.MergeIntervalSeconds([valid, zeroLength, inverted]).Should().Be(3600);
    }

    private static readonly string[] FixPortalOwners = ["FixPortal", "fix-portal"];

    [Theory]
    [InlineData("FixPortal")]
    [InlineData("FixPortal/fixportal-ai-observatory")]
    [InlineData("fix-portal")]
    [InlineData("fix-portal/fixportal-ai-observatory")]
    public void IsAllowedProject_WhenProjectMatchesAllowedOwner_ReturnsTrue(string project)
    {
        ActivityEndpoints.IsAllowedProject(project, FixPortalOwners).Should().BeTrue();
    }

    [Theory]
    [InlineData("fix-portal-other/example")]
    [InlineData("other/fix-portal")]
    [InlineData("claude-review")]
    [InlineData("chris-fixportal/tooling")]
    [InlineData("fixportal/example")]
    public void IsAllowedProject_WhenProjectDoesNotMatchAllowedOwner_ReturnsFalse(string project)
    {
        ActivityEndpoints.IsAllowedProject(project, FixPortalOwners).Should().BeFalse();
    }

    /// <summary>
    /// Empty means allow everything, and empty is the default. A self-hoster who never sets an
    /// owner allowlist must not find their Activity and GitHub tabs silently blank — which is
    /// exactly what a hardcoded "FixPortal" list did to everyone who was not FixPortal.
    /// </summary>
    [Theory]
    [InlineData("someone-else/their-repo")]
    [InlineData("acme")]
    [InlineData("chris-fixportal/tooling")]
    public void IsAllowedProject_WhenNoOwnersConfigured_AllowsEveryProject(string project)
    {
        ActivityEndpoints.IsAllowedProject(project, []).Should().BeTrue();
    }

    [Fact]
    public void BuildDailyActivityResponses_WhenSessionCrossesUtcMidnight_SplitsWallClockAcrossBothDays()
    {
        var sessions = new[]
        {
            new ActivityEndpoints.ActivitySessionSlice(
                "fix-portal/example",
                Instant.FromUtc(2026, 7, 1, 23, 0),
                Instant.FromUtc(2026, 7, 2, 3, 0),
                ActiveSeconds: 14_400
            ),
        };

        var result = ActivityEndpoints.BuildDailyActivityResponses(
            sessions,
            new LocalDate(2026, 7, 1),
            new LocalDate(2026, 7, 2),
            FixPortalOwners
        );

        result.Should().HaveCount(2);
        result[0].Date.Should().Be("2026-07-01");
        result[0].WallClockSeconds.Should().Be(3_600);
        result[0].ActiveSeconds.Should().Be(3_600);
        result[1].Date.Should().Be("2026-07-02");
        result[1].WallClockSeconds.Should().Be(10_800);
        result[1].ActiveSeconds.Should().Be(10_800);
    }

    [Fact]
    public void BuildDailyActivityResponses_FiltersDisallowedProjects()
    {
        var sessions = new[]
        {
            new ActivityEndpoints.ActivitySessionSlice(
                "fix-portal/example",
                Instant.FromUtc(2026, 7, 1, 9, 0),
                Instant.FromUtc(2026, 7, 1, 10, 0),
                ActiveSeconds: 3_600
            ),
            new ActivityEndpoints.ActivitySessionSlice(
                "other/example",
                Instant.FromUtc(2026, 7, 1, 11, 0),
                Instant.FromUtc(2026, 7, 1, 12, 0),
                ActiveSeconds: 3_600
            ),
        };

        var result = ActivityEndpoints.BuildDailyActivityResponses(
            sessions,
            new LocalDate(2026, 7, 1),
            new LocalDate(2026, 7, 1),
            FixPortalOwners
        );

        result.Single().ActiveSeconds.Should().Be(3_600);
    }

    private static readonly LocalDate Today = new(2026, 7, 1);

    [Fact]
    public void TryParseDateRange_WhenBothNull_DefaultsToLast30Days()
    {
        var result = ActivityEndpoints.TryParseDateRange(null, null, Today, out var start, out var end, out var error);

        result.Should().BeTrue();
        error.Should().BeNull();
        // Thirty calendar days inclusive of today — the same default /aggregates uses.
        start.Should().Be(Today.PlusDays(-29));
        end.Should().Be(Today);
    }

    [Fact]
    public void TryParseDateRange_WhenFromAndToValid_ParsesBothDates()
    {
        var result = ActivityEndpoints.TryParseDateRange(
            "2026-06-01",
            "2026-06-15",
            Today,
            out var start,
            out var end,
            out var error
        );

        result.Should().BeTrue();
        error.Should().BeNull();
        start.Should().Be(new LocalDate(2026, 6, 1));
        end.Should().Be(new LocalDate(2026, 6, 15));
    }

    [Fact]
    public void TryParseDateRange_WhenFromInvalid_ReturnsFalseWithError()
    {
        var result = ActivityEndpoints.TryParseDateRange("not-a-date", null, Today, out _, out _, out var error);

        result.Should().BeFalse();
        error.Should().NotBeNull();
    }

    [Fact]
    public void TryParseDateRange_WhenToInvalid_ReturnsFalseWithError()
    {
        var result = ActivityEndpoints.TryParseDateRange(null, "2026-13-99", Today, out _, out _, out var error);

        result.Should().BeFalse();
        error.Should().NotBeNull();
    }
}
