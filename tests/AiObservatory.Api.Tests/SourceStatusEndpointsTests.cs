using AiObservatory.Api.Endpoints;
using AiObservatory.Data.Entities;
using AwesomeAssertions;
using NodaTime;

namespace AiObservatory.Api.Tests;

public class SourceStatusEndpointsTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 8, 24, 12, 0);

    public static TheoryData<SourceSyncState, string> ClassificationCases =>
        new()
        {
            {
                State(configured: false, available: false, failures: 1, lastSuccessAt: Now.Minus(Duration.FromDays(1))),
                "notConfigured"
            },
            {
                State(configured: true, available: false, failures: 1, lastSuccessAt: Now.Minus(Duration.FromDays(1))),
                "unavailable"
            },
            { State(configured: true, available: null, failures: 1, lastSuccessAt: Now), "failing" },
            { State(configured: true, available: true, failures: 0, lastSuccessAt: null), "configured" },
            {
                State(
                    configured: true,
                    available: true,
                    failures: 0,
                    lastSuccessAt: Now.Minus(Duration.FromSeconds(121))
                ),
                "stale"
            },
            { State(configured: true, available: true, failures: 0, lastSuccessAt: Now), "fresh" },
        };

    [Theory]
    [MemberData(nameof(ClassificationCases))]
    public void Classify_ReturnsStatusInRequiredPrecedence(SourceSyncState state, string expected)
    {
        SourceStatusEndpoints.Classify(state, Now).Should().Be(expected);
    }

    [Fact]
    public void Classify_WhenLastSuccessIsExactlyTwiceTheExpectedIntervalAgo_ReturnsFresh()
    {
        var state = State(
            configured: true,
            available: true,
            failures: 0,
            lastSuccessAt: Now.Minus(Duration.FromSeconds(120))
        );

        SourceStatusEndpoints.Classify(state, Now).Should().Be("fresh");
    }

    [Fact]
    public void Classify_WhenExpectedIntervalIsLongMaxValue_RemainsFreshForRepresentableElapsedTime()
    {
        var state = State(
            configured: true,
            available: true,
            failures: 0,
            lastSuccessAt: Now.Minus(Duration.FromDays(1))
        );
        state.ExpectedRefreshIntervalSeconds = long.MaxValue;

        SourceStatusEndpoints.Classify(state, Now).Should().Be("fresh");
    }

    private static SourceSyncState State(bool configured, bool? available, int failures, Instant? lastSuccessAt) =>
        new()
        {
            SourceId = "test-source",
            IsConfigured = configured,
            IsAvailable = available,
            ExpectedRefreshIntervalSeconds = 60,
            LastSuccessAt = lastSuccessAt,
            ConsecutiveFailureCount = failures,
        };
}
