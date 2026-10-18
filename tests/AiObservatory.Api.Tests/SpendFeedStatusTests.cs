using AiObservatory.Api.Endpoints;
using AiObservatory.Api.Services;
using AiObservatory.Data.Entities;
using AwesomeAssertions;
using NodaTime;

namespace AiObservatory.Api.Tests;

public class SpendFeedStatusTests
{
    private static SpendFeedSkip Skip(string reason, long id = 1) => new(id, reason);

    [Fact]
    public void Validate_accepts_a_clean_and_a_skipping_report()
    {
        SpendFeedStatusEndpoints.Validate(new SpendFeedStatusRequest(3, 3, [])).Should().BeNull();
        SpendFeedStatusEndpoints
            .Validate(new SpendFeedStatusRequest(3, 1, [Skip("no-vendor"), Skip("aged-out", 2)]))
            .Should()
            .BeNull();
    }

    [Theory]
    [InlineData(-1, 0, "attempted")]
    [InlineData(1, -1, "posted")]
    [InlineData(1, 2, "posted")]
    public void Validate_rejects_bad_counts(int attempted, int posted, string mentions)
    {
        SpendFeedStatusEndpoints.Validate(new SpendFeedStatusRequest(attempted, posted, [])).Should().Contain(mentions);
    }

    [Theory]
    [InlineData("vendor-name")]
    [InlineData("")]
    [InlineData("No-Vendor")]
    public void Validate_rejects_unknown_reason(string reason)
    {
        SpendFeedStatusEndpoints.Validate(new SpendFeedStatusRequest(2, 1, [Skip(reason)])).Should().Contain("reason");
    }

    [Fact]
    public void Validate_rejects_non_positive_expense_id_and_oversized_list()
    {
        SpendFeedStatusEndpoints
            .Validate(new SpendFeedStatusRequest(2, 1, [Skip("no-vendor", 0)]))
            .Should()
            .Contain("expenseId");

        var tooMany = Enumerable.Range(1, 5001).Select(i => Skip("no-vendor", i)).ToArray();
        SpendFeedStatusEndpoints.Validate(new SpendFeedStatusRequest(6000, 999, tooMany)).Should().Contain("5000");
    }

    [Fact]
    public void Validate_rejects_a_null_skipped_list()
    {
        SpendFeedStatusEndpoints.Validate(new SpendFeedStatusRequest(1, 1, null!)).Should().Contain("skipped");
    }

    [Fact]
    public void Validate_rejects_a_null_skipped_entry()
    {
        SpendFeedStatusEndpoints.Validate(new SpendFeedStatusRequest(1, 0, [null!])).Should().Contain("skipped");
    }

    [Fact]
    public void DescribeSkips_counts_reasons_largest_first_and_carries_nothing_else()
    {
        var text = SpendFeedStatusEndpoints.DescribeSkips([
            Skip("no-category", 7),
            Skip("no-vendor", 8),
            Skip("no-vendor", 9),
        ]);

        text.Should().Be("3 expenses skipped: 2 no-vendor, 1 no-category");
    }

    [Fact]
    public void DescribeSkips_uses_the_singular_for_one()
    {
        SpendFeedStatusEndpoints.DescribeSkips([Skip("aged-out")]).Should().Be("1 expense skipped: 1 aged-out");
    }

    [Fact]
    public void Skipping_state_classifies_unavailable_and_clean_state_classifies_fresh()
    {
        var now = Instant.FromUtc(2030, 1, 15, 12, 0);
        var skipping = new SourceSyncState
        {
            SourceId = SpendFeedStatusEndpoints.SourceId,
            IsConfigured = true,
            IsAvailable = false,
            ConsecutiveFailureCount = 1,
            LastAttemptAt = now,
            ExpectedRefreshIntervalSeconds = (long)SpendFeedStatusEndpoints.ExpectedRefreshInterval.TotalSeconds,
        };
        var clean = new SourceSyncState
        {
            SourceId = SpendFeedStatusEndpoints.SourceId,
            IsConfigured = true,
            IsAvailable = true,
            LastAttemptAt = now,
            LastSuccessAt = now,
            ExpectedRefreshIntervalSeconds = (long)SpendFeedStatusEndpoints.ExpectedRefreshInterval.TotalSeconds,
        };

        SourceHealthClassifier.Classify(skipping, now).Should().Be(SourceHealthClassifier.Unavailable);
        SourceHealthClassifier.Classify(clean, now).Should().Be(SourceHealthClassifier.Fresh);
    }
}
