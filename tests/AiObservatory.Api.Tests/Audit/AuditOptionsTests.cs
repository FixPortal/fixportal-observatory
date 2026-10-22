using AiObservatory.Api.Audit;
using AwesomeAssertions;
using NodaTime;
using NodaTime.Testing;

namespace AiObservatory.Api.Tests.Audit;

public sealed class AuditOptionsTests
{
    /// <summary>Startup rejects finite expiries beyond the actual clock boundary without rejecting indefinite retention.</summary>
    [Fact]
    public void Validate_InstantBoundary_RejectsUnrepresentableExpiryAndAllowsIndefinite()
    {
        var validator = new AuditOptionsValidator(new FakeClock(Instant.MaxValue - Duration.FromHours(1)));
        validator
            .Validate(
                null,
                new()
                {
                    Enabled = true,
                    EvidenceRetentionDays = 1,
                    SummaryRetentionDays = null,
                }
            )
            .Failed.Should()
            .BeTrue();
        validator
            .Validate(
                null,
                new()
                {
                    Enabled = true,
                    EvidenceRetentionDays = null,
                    SummaryRetentionDays = 1,
                }
            )
            .Failed.Should()
            .BeTrue();
        validator
            .Validate(
                null,
                new()
                {
                    Enabled = true,
                    EvidenceRetentionDays = null,
                    SummaryRetentionDays = null,
                }
            )
            .Succeeded.Should()
            .BeTrue();
    }

    private static AuditOptionsValidator Validator() => new(new FakeClock(Instant.FromUtc(2026, 10, 22, 12, 0)));

    /// <summary>Both Duration-overflow and Instant-overflow finite values are rejected before activation.</summary>
    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(3_000_000)]
    [InlineData(36_501)]
    public void Validate_UnsupportedFiniteRetention_RejectsBothSettings(int days)
    {
        var validator = Validator();
        validator.Validate(null, new() { Enabled = true, EvidenceRetentionDays = days }).Failed.Should().BeTrue();
        validator.Validate(null, new() { Enabled = true, SummaryRetentionDays = days }).Failed.Should().BeTrue();
    }

    /// <summary>Finite values at the supported upper bound remain valid alongside explicit indefinite retention.</summary>
    [Fact]
    public void Validate_SupportedFiniteRetention_AcceptsBoundary()
    {
        Validator()
            .Validate(
                null,
                new()
                {
                    Enabled = true,
                    EvidenceRetentionDays = 36_500,
                    SummaryRetentionDays = 36_500,
                }
            )
            .Succeeded.Should()
            .BeTrue();
    }

    /// <summary>The dormant default still supplies all release limits when explicitly enabled.</summary>
    [Fact]
    public void Validate_Defaults_UsesApprovedBoundsAndDormantFlag()
    {
        var options = new AuditOptions();
        options.Enabled.Should().BeFalse();
        options.MaxChunkBytes.Should().Be(8 * 1024 * 1024);
        options.MaxCaptureBytes.Should().Be(512L * 1024 * 1024);
        options.MaxSnapshotBytes.Should().Be(512L * 1024 * 1024);
        options.MaxRecordsPerCapture.Should().Be(1_000_000);
        options.MaxRunWindowDays.Should().Be(31);
        options.MaxExecutingRuns.Should().Be(1);
        options.MaxOutstandingRequestsPerMachine.Should().Be(2);
        options.MaxQueuedRuns.Should().Be(20);
        options.MaxExecutionAttempts.Should().Be(3);
        options.CaptureRequestDeadline.Should().Be(TimeSpan.FromHours(24));
        options.RunLeaseDuration.Should().Be(TimeSpan.FromMinutes(5));
        options.RunLeaseRenewalInterval.Should().Be(TimeSpan.FromMinutes(1));
        options.EvidenceRetentionDays.Should().Be(90);
        options.SummaryRetentionDays.Should().Be(365);
        Validator().Validate(null, options).Succeeded.Should().BeTrue();
        options.Enabled = true;
        Validator().Validate(null, options).Succeeded.Should().BeTrue();
    }

    /// <summary>Zero or negative intervals cannot create immediately expired or immortal execution leases.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_InvalidDurations_RejectsWhenEnabled(int seconds)
    {
        foreach (
            var property in new[]
            {
                nameof(AuditOptions.RunLeaseDuration),
                nameof(AuditOptions.RunLeaseRenewalInterval),
                nameof(AuditOptions.CaptureRequestDeadline),
            }
        )
        {
            var options = new AuditOptions { Enabled = true };
            typeof(AuditOptions).GetProperty(property)!.SetValue(options, TimeSpan.FromSeconds(seconds));
            Validator().Validate(null, options).Failed.Should().BeTrue(property);
        }
    }

    /// <summary>Oversized bounds and renewal intervals are rejected before activation.</summary>
    [Fact]
    public void Validate_UnsafeLimits_RejectsEachBound()
    {
        foreach (
            var property in new[]
            {
                nameof(AuditOptions.MaxChunkBytes),
                nameof(AuditOptions.MaxCaptureBytes),
                nameof(AuditOptions.MaxSnapshotBytes),
                nameof(AuditOptions.MaxRecordsPerCapture),
                nameof(AuditOptions.MaxRunWindowDays),
                nameof(AuditOptions.MaxExecutingRuns),
                nameof(AuditOptions.MaxOutstandingRequestsPerMachine),
                nameof(AuditOptions.MaxQueuedRuns),
                nameof(AuditOptions.MaxExecutionAttempts),
            }
        )
        {
            var options = new AuditOptions { Enabled = true };
            var member = typeof(AuditOptions).GetProperty(property)!;
            var current = Convert.ToInt64(member.GetValue(options));
            member.SetValue(options, Convert.ChangeType(current + 1, member.PropertyType));
            Validator().Validate(null, options).Failed.Should().BeTrue(property);
            member.SetValue(options, Convert.ChangeType(0, member.PropertyType));
            Validator().Validate(null, options).Failed.Should().BeTrue(property);
        }
        Validator()
            .Validate(null, new() { Enabled = true, RunLeaseRenewalInterval = TimeSpan.FromMinutes(5) })
            .Failed.Should()
            .BeTrue();
    }

    /// <summary>Indefinite retention is explicit, while nonpositive finite retention is invalid.</summary>
    [Fact]
    public void Validate_RetentionAndDisabled_RespectsExplicitIndefinite()
    {
        var validator = Validator();
        validator
            .Validate(
                null,
                new()
                {
                    Enabled = true,
                    EvidenceRetentionDays = null,
                    SummaryRetentionDays = null,
                }
            )
            .Succeeded.Should()
            .BeTrue();
        validator.Validate(null, new() { Enabled = true, EvidenceRetentionDays = 0 }).Failed.Should().BeTrue();
        validator.Validate(null, new() { Enabled = true, SummaryRetentionDays = -1 }).Failed.Should().BeTrue();
        validator.Validate(null, new() { MaxChunkBytes = 0 }).Succeeded.Should().BeTrue();
    }
}
