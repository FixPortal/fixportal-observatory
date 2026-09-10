using System.Text.Json;
using AiObservatory.Api.Services.GitHub;
using AwesomeAssertions;

namespace AiObservatory.Api.Tests.Services;

/// <summary>
/// The month marker every GitHub charge is filed under. A rejected date is not a loud failure:
/// the sync's caller catches broadly, so it degrades to "no GitHub spend recorded" — the exact
/// understatement this arm exists to fix.
/// </summary>
public class GitHubBillingDateConverterTests
{
    private static readonly JsonSerializerOptions Options = new() { Converters = { new GitHubBillingDateConverter() } };

    private static DateOnly Read(string json) => JsonSerializer.Deserialize<DateOnly>(json, Options);

    [Fact]
    public void ReadsTheDocumentedDateOnlyShape()
    {
        Read("\"2026-07-01\"").Should().Be(new DateOnly(2026, 7, 1));
    }

    [Fact]
    public void ReadsTheTimestampTheLiveApiActuallyReturns()
    {
        Read("\"2026-07-01T00:00:00Z\"").Should().Be(new DateOnly(2026, 7, 1));
    }

    /// <summary>
    /// Read through the machine's time zone instead of UTC, a midnight-UTC month marker slips
    /// into the previous month anywhere west of Greenwich — filing a whole month's GitHub spend
    /// under the wrong period. This offset is the case that exposes it.
    /// </summary>
    [Fact]
    public void AnchorsTheTimestampToUtcRatherThanTheMachineTimeZone()
    {
        Read("\"2026-07-01T00:00:00+05:00\"")
            .Should()
            .Be(
                new DateOnly(2026, 6, 30),
                "the instant is 2026-06-30T19:00Z, and UTC is what decides the billing month"
            );
    }

    /// <summary>
    /// A timestamp with no offset must be read as UTC, not as machine-local. Read as local it
    /// is then converted to UTC and a midnight month marker slides into the previous month on
    /// any machine east of Greenwich — a whole month of GitHub spend filed under the wrong
    /// period, on some machines only. GitHub currently always sends the <c>Z</c>, so this
    /// guards a latent trap rather than a live one.
    /// </summary>
    [Fact]
    public void ReadsAnOffsetLessTimestampAsUtcNotAsMachineLocalTime()
    {
        Read("\"2026-07-01T00:00:00\"").Should().Be(new DateOnly(2026, 7, 1));
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"not a date\"")]
    [InlineData("\"2026-13-01\"")]
    public void ThrowsOnAFormatItDoesNotRecognise(string json)
    {
        var act = () => Read(json);

        act.Should().Throw<JsonException>("a format change must surface, not quietly become zero recorded spend");
    }

    /// <summary>
    /// Documenting a ceiling rather than endorsing it. The fallback parse is lenient, so a
    /// slash-separated date is accepted and read MONTH-first under the invariant culture. If
    /// GitHub ever switched to a day-first format the misread would be silent — 01/07 would
    /// book July's spend into January. Tighten the fallback to a known set of formats if that
    /// ever becomes a real possibility; today GitHub sends ISO 8601 only.
    /// </summary>
    [Fact]
    public void ReadsASlashDateMonthFirst()
    {
        Read("\"01/07/2026\"").Should().Be(new DateOnly(2026, 1, 7));
    }

    [Fact]
    public void ThrowsOnANullDate()
    {
        var act = () => Read("null");

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void WritesTheDateOnlyShapeBack()
    {
        JsonSerializer.Serialize(new DateOnly(2026, 7, 1), Options).Should().Be("\"2026-07-01\"");
    }
}
