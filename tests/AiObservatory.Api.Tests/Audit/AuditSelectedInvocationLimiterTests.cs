using AiObservatory.Api.Audit;
using AwesomeAssertions;
using NodaTime;
using NodaTime.Testing;

namespace AiObservatory.Api.Tests.Audit;

public sealed class AuditSelectedInvocationLimiterTests
{
    [Fact]
    public void GetAndPostShareOneAuthorityCooldownAndInventoryIsIndependent()
    {
        var clock = new FakeClock(Instant.FromUtc(2026, 10, 23, 12, 0));
        var selected = new AuditSelectedInvocationLimiter(clock);
        selected.TryAcquire("verified-user").Should().BeTrue("GET takes the common bucket");
        selected.TryAcquire("verified-user").Should().BeFalse("a subsequent POST uses that same bucket");
        new OperationalInventoryLimiter(clock).TryAcquire("verified-user").Should().BeTrue();
        selected.TryAcquire("other-verified-user").Should().BeTrue();
        clock.Advance(Duration.FromSeconds(59));
        selected.TryAcquire("verified-user").Should().BeFalse();
        clock.Advance(Duration.FromSeconds(1));
        selected.TryAcquire("verified-user").Should().BeTrue();
    }

    [Fact]
    public void BoundedPartitionExhaustionFailsClosedUntilCooldownExpires()
    {
        var clock = new FakeClock(Instant.FromUtc(2026, 10, 23, 12, 0));
        var limiter = new AuditSelectedInvocationLimiter(clock);
        for (var index = 0; index < 128; index++)
        {
            limiter.TryAcquire("verified-" + index).Should().BeTrue();
        }
        limiter.TryAcquire("verified-overflow").Should().BeFalse();
        limiter.TryAcquire("verified-0").Should().BeFalse();
        clock.Advance(Duration.FromMinutes(1));
        limiter.TryAcquire("verified-overflow").Should().BeTrue();
    }
}
