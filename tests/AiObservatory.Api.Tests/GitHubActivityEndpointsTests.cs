using AiObservatory.Api.Endpoints;
using AwesomeAssertions;
using NodaTime;

namespace AiObservatory.Api.Tests;

public class GitHubActivityEndpointsTests
{
    [Fact]
    public void ComputeTurnaroundHours_WhenFirstReviewAtIsNull_ReturnsNull()
    {
        var result = GitHubActivityEndpoints.ComputeTurnaroundHours(Instant.FromUtc(2026, 7, 1, 9, 0), null);
        result.Should().BeNull();
    }

    [Fact]
    public void ComputeTurnaroundHours_WhenReviewedThreeHoursLater_ReturnsThree()
    {
        var result = GitHubActivityEndpoints.ComputeTurnaroundHours(
            Instant.FromUtc(2026, 7, 1, 9, 0),
            Instant.FromUtc(2026, 7, 1, 12, 0)
        );
        result.Should().Be(3.0);
    }

    [Fact]
    public void ComputeTurnaroundHours_RoundsToOneDecimalPlace()
    {
        var result = GitHubActivityEndpoints.ComputeTurnaroundHours(
            Instant.FromUtc(2026, 7, 1, 9, 0),
            Instant.FromUtc(2026, 7, 1, 9, 40)
        );
        result.Should().Be(0.7); // 40 minutes = 0.666...h, rounds to 0.7
    }

    [Fact]
    public void ComputeSuccessRate_WhenNoRuns_ReturnsZero()
    {
        GitHubActivityEndpoints.ComputeSuccessRate(0, 0).Should().Be(0);
    }

    [Fact]
    public void ComputeSuccessRate_MixedBatchWithNonTerminalStates_CountsOnlyExplicitSuccesses()
    {
        // 5 runs: 2 success, 1 failure, 1 cancelled, 1 in_progress.
        // Old (broken) formula: (total - failed) / total = 4/5 = 80%.
        // Correct formula: succeeded / total = 2/5 = 40% — cancelled/in_progress must not
        // count as success just because they aren't "failure".
        GitHubActivityEndpoints.ComputeSuccessRate(total: 5, succeeded: 2).Should().Be(40.0);
    }

    [Fact]
    public void ComputeSuccessRate_AllSuccess_ReturnsOneHundred()
    {
        GitHubActivityEndpoints.ComputeSuccessRate(total: 3, succeeded: 3).Should().Be(100.0);
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("timed_out")]
    [InlineData("startup_failure")]
    public void IsTerminalFailure_WhenStatusIsTerminalFailure_ReturnsTrue(string status)
    {
        GitHubActivityEndpoints.IsTerminalFailure(status).Should().BeTrue();
    }

    [Theory]
    [InlineData("success")]
    [InlineData("cancelled")]
    [InlineData("in_progress")]
    [InlineData("queued")]
    public void IsTerminalFailure_WhenStatusIsNotTerminalFailure_ReturnsFalse(string status)
    {
        GitHubActivityEndpoints.IsTerminalFailure(status).Should().BeFalse();
    }
}
