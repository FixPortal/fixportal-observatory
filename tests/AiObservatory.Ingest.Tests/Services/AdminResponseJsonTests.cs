using System.Text.Json;
using AiObservatory.Ingest.Services;
using AwesomeAssertions;
using Xunit;

namespace AiObservatory.Ingest.Tests.Services;

public sealed class AdminResponseJsonTests
{
    public static TheoryData<string, bool, string> InvalidCursors =>
        new()
        {
            { "{}", false, "response is missing next_page." },
            { "{}", true, "response is missing next_page." },
            { "{\"next_page\":\"cursor\"}", false, "final page has an unexpected cursor." },
            { "{\"next_page\":0}", false, "final page has an unexpected cursor." },
            { "{\"next_page\":null}", true, "response requires a non-empty next_page cursor." },
            { "{\"next_page\":0}", true, "response requires a non-empty next_page cursor." },
            { "{\"next_page\":true}", true, "response requires a non-empty next_page cursor." },
            { "{\"next_page\":{}}", true, "response requires a non-empty next_page cursor." },
            { "{\"next_page\":[]}", true, "response requires a non-empty next_page cursor." },
            { "{\"next_page\":\"\"}", true, "response requires a non-empty next_page cursor." },
            { "{\"next_page\":\" \\t\\n\"}", true, "response requires a non-empty next_page cursor." },
        };

    [Theory]
    [MemberData(nameof(InvalidCursors))]
    public void RequireCursor_PreservesProviderDiagnostics(string json, bool hasMore, string diagnostic)
    {
        using var document = JsonDocument.Parse(json);
        foreach (var provider in new[] { "Anthropic", "OpenAI" })
        {
            var act = () => AdminResponseJson.RequireCursor(document.RootElement, hasMore, provider);
            act.Should().Throw<InvalidDataException>().Which.Message.Should().Be($"{provider} {diagnostic}");
        }
    }

    [Theory]
    [InlineData("{\"next_page\":null}", false, null)]
    [InlineData("{\"next_page\":\" cursor+/= \"}", true, " cursor+/= ")]
    public void RequireCursor_AcceptsFinalNullAndReturnsContinuationUnchanged(
        string json,
        bool hasMore,
        string? expected
    )
    {
        using var document = JsonDocument.Parse(json);
        AdminResponseJson.RequireCursor(document.RootElement, hasMore, "Anthropic").Should().Be(expected);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"currency\":null}")]
    [InlineData("{\"currency\":0}")]
    [InlineData("{\"currency\":true}")]
    [InlineData("{\"currency\":{}}")]
    [InlineData("{\"currency\":[]}")]
    [InlineData("{\"currency\":\"\"}")]
    [InlineData("{\"currency\":\" \\t\\n\"}")]
    public void RequireNonBlankString_PreservesProviderAndPropertyDiagnostics(string json)
    {
        using var document = JsonDocument.Parse(json);
        foreach (var provider in new[] { "Anthropic", "OpenAI" })
        {
            var act = () => AdminResponseJson.RequireNonBlankString(document.RootElement, "currency", provider);
            act.Should()
                .Throw<InvalidDataException>()
                .Which.Message.Should()
                .Be($"{provider} response is missing currency.");
        }
    }

    [Fact]
    public void RequireNonBlankString_ReturnsValueWithoutTrimming()
    {
        using var document = JsonDocument.Parse("{\"currency\":\" USD \"}");
        AdminResponseJson.RequireNonBlankString(document.RootElement, "currency", "OpenAI").Should().Be(" USD ");
    }

    [Theory]
    [InlineData("{}", null, false)]
    [InlineData("{\"model\":null}", null, false)]
    [InlineData("{\"model\":\" value \"}", " value ", false)]
    [InlineData("{\"model\":0}", null, true)]
    [InlineData("{\"model\":true}", null, true)]
    [InlineData("{\"model\":{}}", null, true)]
    [InlineData("{\"model\":[]}", null, true)]
    [InlineData("{\"model\":\"\"}", null, true)]
    [InlineData("{\"model\":\" \\t\\n\"}", null, true)]
    public void OptionalNonBlankString_PreservesValuesAndProviderDiagnostics(
        string json,
        string? expected,
        bool invalid
    )
    {
        using var document = JsonDocument.Parse(json);
        foreach (var provider in new[] { "Anthropic", "OpenAI" })
        {
            var act = () => AdminResponseJson.OptionalNonBlankString(document.RootElement, "model", provider);
            if (invalid)
            {
                act.Should()
                    .Throw<InvalidDataException>()
                    .Which.Message.Should()
                    .Be($"{provider} model must be a non-empty string or null.");
            }
            else
            {
                act().Should().Be(expected);
            }
        }
    }
}
