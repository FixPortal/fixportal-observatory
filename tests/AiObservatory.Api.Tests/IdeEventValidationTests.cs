using System.Text;
using AiObservatory.Api.Endpoints;
using AwesomeAssertions;
using NodaTime;

namespace AiObservatory.Api.Tests;

public sealed class IdeEventValidationTests
{
    [Fact]
    public void AcceptsTheByteExactIdeEnvelope()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "routing.decided.v1.json"));

        var envelope = IdeEndpoints.ParseEvent(bytes, Instant.FromUtc(2026, 8, 4, 12, 1));

        envelope.EventType.Should().Be("routing.decided");
        envelope.Classification.Should().Be(0);
        envelope.Identity.MissionId.Value.Should().Be(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    }

    [Fact]
    public void AcceptsAnEnvelopeWithinTheClockSkewAllowance()
    {
        var valid = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "routing.decided.v1.json"));
        // occurredAt 4 minutes ahead of the server's clock: inside the 5-minute allowance.
        var skewed = valid.Replace("2026-08-04T12:00:00Z", "2026-08-04T12:05:00Z", StringComparison.Ordinal);

        var envelope = IdeEndpoints.ParseEvent(Encoding.UTF8.GetBytes(skewed), Instant.FromUtc(2026, 8, 4, 12, 1));

        envelope.OccurredAt.Should().Be(Instant.FromUtc(2026, 8, 4, 12, 5));
    }

    [Theory]
    [MemberData(nameof(InvalidEnvelopes))]
    public void RejectsContentOrIdentityOutsideTheClosedContract(string json)
    {
        var parse = () => IdeEndpoints.ParseEvent(Encoding.UTF8.GetBytes(json), Instant.FromUtc(2026, 8, 4, 12, 1));

        parse.Should().Throw<InvalidDataException>();
    }

    public static TheoryData<string> InvalidEnvelopes
    {
        get
        {
            var valid = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "routing.decided.v1.json"));
            return
            [
                valid.Replace("\"routing.decided\"", "\"unknown.event\"", StringComparison.Ordinal),
                valid.Replace("\"classification\":0", "\"classification\":1", StringComparison.Ordinal),
                valid.Replace(
                    "753cb584-cd0b-4e16-9f08-6c0ce130a84a",
                    "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                    StringComparison.Ordinal
                ),
                valid.Replace("\"role\":\"implementer\"", "\"role\":\" \"", StringComparison.Ordinal),
                valid.Replace(
                    "\"routingRuleVersion\":\"assisted-v1\"",
                    "\"routingRuleVersion\":\"assisted-v1\",\"rawPrompt\":\"no\"",
                    StringComparison.Ordinal
                ),
                valid.Replace(
                    "\"selectedModelId\":\"gpt-5.6-sol\"",
                    "\"selectedModelId\":\"gpt-5.6-sol\",\"selectedModelId\":\"claude-fable-5\"",
                    StringComparison.Ordinal
                ),
                valid.Replace(
                    "\"classification\":0",
                    "\"classification\":0,\"unknown\":true",
                    StringComparison.Ordinal
                ),
                // Beyond the 5-minute clock-skew allowance shared with /api/events.
                valid.Replace("2026-08-04T12:00:00Z", "2026-08-04T12:07:00Z", StringComparison.Ordinal),
                // A malformed occurredAt throws inside the NodaTime STJ converter; System.Text.Json
                // wraps converter failures in JsonException, so this must surface as
                // InvalidDataException (400), never escape as a raw FormatException (500).
                valid.Replace("2026-08-04T12:00:00Z", "not-a-timestamp", StringComparison.Ordinal),
                // Duplicate envelope members must reject like /api/events does, not parse last-wins.
                valid.Replace(
                    "\"idempotencyKey\":\"",
                    "\"idempotencyKey\":\"shadow\",\"idempotencyKey\":\"",
                    StringComparison.Ordinal
                ),
                // Missing identity members must reject as InvalidDataException, not crash on null dereference.
                valid.Replace(
                    "\"partnerId\":{\"value\":\"753cb584-cd0b-4e16-9f08-6c0ce130a84a\"},",
                    "",
                    StringComparison.Ordinal
                ),
                valid.Replace(
                    "\"missionId\":{\"value\":\"11111111-1111-1111-1111-111111111111\"},",
                    "",
                    StringComparison.Ordinal
                ),
            ];
        }
    }
}
