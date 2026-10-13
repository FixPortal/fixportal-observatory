using System.Text;
using System.Text.Json.Nodes;
using AiObservatory.Api.Endpoints;
using AwesomeAssertions;
using NodaTime;

namespace AiObservatory.Api.Tests;

/// <summary>
/// Payload rules for the two IDE event types that <see cref="IdeEventValidationTests"/> never
/// constructs: run.completed and operator.intervened. The envelope is the byte-exact
/// routing.decided fixture with only eventType and payload swapped, so each case isolates the
/// payload check it names.
/// </summary>
public sealed class IdeEventPayloadValidationTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 8, 4, 12, 1);

    public static TheoryData<string, string> ValidPayloads =>
        new()
        {
            {
                "run.completed",
                """{"modelId":"m","adapterId":"a","durationMilliseconds":0,"terminalStatus":"succeeded"}"""
            },
            {
                "run.completed",
                """{"modelId":"m","adapterId":"a","durationMilliseconds":9223372036854775807,"terminalStatus":"failed"}"""
            },
            {
                "run.completed",
                """{"modelId":"m","adapterId":"a","durationMilliseconds":1500,"terminalStatus":"cancelled"}"""
            },
            { "operator.intervened", """{"modelId":"m","reasonCode":"operator-input"}""" },
            { "operator.intervened", """{"modelId":"m","reasonCode":"manual-override"}""" },
            { "operator.intervened", """{"modelId":"m","reasonCode":"approval"}""" },
        };

    public static TheoryData<string, string> InvalidPayloads =>
        new()
        {
            // run.completed: duration must be a non-negative Int64
            {
                "run.completed",
                """{"modelId":"m","adapterId":"a","durationMilliseconds":-1,"terminalStatus":"succeeded"}"""
            },
            {
                "run.completed",
                """{"modelId":"m","adapterId":"a","durationMilliseconds":1.5,"terminalStatus":"succeeded"}"""
            },
            {
                "run.completed",
                """{"modelId":"m","adapterId":"a","durationMilliseconds":9223372036854775808,"terminalStatus":"succeeded"}"""
            },
            {
                "run.completed",
                """{"modelId":"m","adapterId":"a","durationMilliseconds":"5","terminalStatus":"succeeded"}"""
            },
            // run.completed: terminalStatus is a closed set
            {
                "run.completed",
                """{"modelId":"m","adapterId":"a","durationMilliseconds":5,"terminalStatus":"done"}"""
            },
            {
                "run.completed",
                """{"modelId":"m","adapterId":"a","durationMilliseconds":5,"terminalStatus":"Succeeded"}"""
            },
            // operator.intervened: reasonCode is a closed set
            { "operator.intervened", """{"modelId":"m","reasonCode":"other"}""" },
            { "operator.intervened", """{"modelId":"m","reasonCode":"Approval"}""" },
            // member type, presence and shape
            {
                "run.completed",
                """{"modelId":5,"adapterId":"a","durationMilliseconds":5,"terminalStatus":"succeeded"}"""
            },
            { "run.completed", """{"modelId":"m","adapterId":"a","durationMilliseconds":5}""" },
            {
                "run.completed",
                """{"modelId":"m","adapterId":"a","durationMilliseconds":5,"terminalStatus":"succeeded","rawPrompt":"no"}"""
            },
            { "operator.intervened", """{"modelId":"m"}""" },
            { "operator.intervened", """{"modelId":"m","reasonCode":"approval","note":"no"}""" },
            { "operator.intervened", """{"modelId":" m","reasonCode":"approval"}""" },
            { "operator.intervened", """{"modelId":"","reasonCode":"approval"}""" },
            { "operator.intervened", """["modelId","reasonCode"]""" },
            // a payload of another type's shape
            { "operator.intervened", """{"selectedModelId":"m","snapshotId":"s","routingRuleVersion":"v"}""" },
        };

    [Theory]
    [MemberData(nameof(ValidPayloads))]
    public void AcceptsAWellFormedPayload(string eventType, string payload)
    {
        var envelope = IdeEndpoints.ParseEvent(Envelope(eventType, payload), Now);

        envelope.EventType.Should().Be(eventType);
    }

    [Theory]
    [MemberData(nameof(InvalidPayloads))]
    public void RejectsAPayloadOutsideTheClosedContract(string eventType, string payload)
    {
        var parse = () => IdeEndpoints.ParseEvent(Envelope(eventType, payload), Now);

        parse.Should().Throw<InvalidDataException>();
    }

    private static byte[] Envelope(string eventType, string payloadJson)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "routing.decided.v1.json");
        var envelope = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        envelope["eventType"] = eventType;
        envelope["payload"] = JsonNode.Parse(payloadJson);
        return Encoding.UTF8.GetBytes(envelope.ToJsonString());
    }
}
