using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;

namespace AiObservatory.Api.Tests;

/// <summary>
/// A8: FixedWindowRateLimiterOptions is only validated when a partition's limiter is first
/// constructed, so a zero or negative RateLimiting:ApiPermitLimit used to 500 every /api
/// request per new client IP instead of refusing to start.
/// </summary>
public class ProgramRateLimitValidationTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void RejectsANonPositivePermitLimitAtBoot(string value)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["RateLimiting:ApiPermitLimit"] = value;

        var act = () => Program.ValidateRateLimitPermitLimit(builder);

        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("1")]
    [InlineData("120")]
    public void AcceptsTheDefaultOrAnyPositivePermitLimit(string? value)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["RateLimiting:ApiPermitLimit"] = value;

        var act = () => Program.ValidateRateLimitPermitLimit(builder);

        act.Should().NotThrow();
    }
}
