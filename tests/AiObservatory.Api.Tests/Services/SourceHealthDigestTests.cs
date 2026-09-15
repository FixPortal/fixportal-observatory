using AiObservatory.Api.Services;
using AiObservatory.Data.Entities;
using AwesomeAssertions;
using NodaTime;

namespace AiObservatory.Api.Tests.Services;

public class SourceHealthDigestTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 9, 15, 12, 0);

    [Fact]
    public void Compose_WhenEverySourceIsHealthy_ReturnsNull()
    {
        var states = new[]
        {
            State("openai-pricing", configured: true, available: true, failures: 0, lastSuccessAt: Now),
        };

        SourceHealthDigest.Compose(states, Now).Should().BeNull();
    }

    [Fact]
    public void Compose_WhenOnlyStaleOrUnconfiguredSourcesExist_ReturnsNull()
    {
        // Local producers go stale legitimately whenever the machine-side sweep has not run,
        // and an unconfigured source is a deliberate choice. Sending for either would train
        // the reader to ignore the digest, which costs more than the alert is worth.
        var states = new[]
        {
            State(
                "copilot-local",
                configured: true,
                available: true,
                failures: 0,
                lastSuccessAt: Now.Minus(Duration.FromDays(5))
            ),
            State("openai-usage-api", configured: false, available: null, failures: 0, lastSuccessAt: null),
        };

        SourceHealthDigest.Compose(states, Now).Should().BeNull();
    }

    [Theory]
    [InlineData(false, 306, "unavailable")]
    [InlineData(null, 45, "failing")]
    public void Compose_WhenASourceIsDegraded_ReturnsADigestNamingIt(
        bool? available,
        int failures,
        string expectedStatus
    )
    {
        var states = new[]
        {
            State(
                "github-activity-api",
                configured: true,
                available: available,
                failures: failures,
                lastSuccessAt: Now.Minus(Duration.FromDays(12)),
                lastError: "27 of 27 configured GitHub repos failed to ingest this cycle"
            ),
        };

        var digest = SourceHealthDigest.Compose(states, Now);

        digest.Should().NotBeNull();
        digest.DegradedCount.Should().Be(1);
        digest.Body.Should().Contain("github-activity-api").And.Contain(expectedStatus);
        // The failure count and the last error are the two facts that make the message
        // actionable without opening the dashboard; assert both so a future reformat
        // cannot quietly drop them.
        digest.Body.Should().Contain(failures.ToString(System.Globalization.CultureInfo.InvariantCulture));
        digest.Body.Should().Contain("27 of 27 configured GitHub repos failed");
    }

    [Fact]
    public void Compose_ListsUnavailableBeforeFailingAndHigherFailureCountsFirst()
    {
        var states = new[]
        {
            State(
                "kimi-pricing",
                configured: true,
                available: null,
                failures: 50,
                lastSuccessAt: Now.Minus(Duration.FromDays(16))
            ),
            State(
                "github-activity-api",
                configured: true,
                available: false,
                failures: 306,
                lastSuccessAt: Now.Minus(Duration.FromDays(12))
            ),
            State(
                "claude-pricing",
                configured: true,
                available: null,
                failures: 45,
                lastSuccessAt: Now.Minus(Duration.FromDays(14))
            ),
        };

        var digest = SourceHealthDigest.Compose(states, Now);

        digest.Should().NotBeNull();
        digest.DegradedCount.Should().Be(3);
        var githubIndex = digest.Body.IndexOf("github-activity-api", StringComparison.Ordinal);
        var kimiIndex = digest.Body.IndexOf("kimi-pricing", StringComparison.Ordinal);
        var claudeIndex = digest.Body.IndexOf("claude-pricing", StringComparison.Ordinal);
        githubIndex.Should().BeLessThan(kimiIndex, "unavailable outranks failing");
        kimiIndex.Should().BeLessThan(claudeIndex, "within a status, more consecutive failures first");
    }

    [Fact]
    public void Compose_MentionsStaleSourcesInTheBodyWithoutCountingThemAsDegraded()
    {
        var states = new[]
        {
            State(
                "github-activity-api",
                configured: true,
                available: false,
                failures: 306,
                lastSuccessAt: Now.Minus(Duration.FromDays(12))
            ),
            State(
                "copilot-local",
                configured: true,
                available: true,
                failures: 0,
                lastSuccessAt: Now.Minus(Duration.FromDays(5))
            ),
        };

        var digest = SourceHealthDigest.Compose(states, Now);

        digest.Should().NotBeNull();
        digest.DegradedCount.Should().Be(1);
        digest.Body.Should().Contain("copilot-local");
    }

    [Fact]
    public void Compose_SubjectCarriesTheDegradedCount()
    {
        var states = new[]
        {
            State("github-activity-api", configured: true, available: false, failures: 306, lastSuccessAt: null),
            State("claude-pricing", configured: true, available: null, failures: 45, lastSuccessAt: null),
        };

        SourceHealthDigest.Compose(states, Now)!.Subject.Should().Contain("2");
    }

    private static SourceSyncState State(
        string sourceId,
        bool configured,
        bool? available,
        int failures,
        Instant? lastSuccessAt,
        string? lastError = null
    ) =>
        new()
        {
            SourceId = sourceId,
            IsConfigured = configured,
            IsAvailable = available,
            ConsecutiveFailureCount = failures,
            LastSuccessAt = lastSuccessAt,
            LastAttemptAt = lastSuccessAt,
            LastError = lastError,
            ExpectedRefreshIntervalSeconds = 60,
        };
}
