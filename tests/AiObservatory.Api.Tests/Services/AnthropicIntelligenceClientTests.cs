using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AiObservatory.Api.Services.Intelligence;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiObservatory.Api.Tests.Services;

public class AnthropicIntelligenceClientTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankApiKeyLeavesTheClientDisabled(string? apiKey)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ANTHROPIC_API_KEY"] = apiKey })
            .Build();
        var sut = new AnthropicIntelligenceClient(configuration, NullLogger<AnthropicIntelligenceClient>.Instance);

        sut.IsConfigured.Should().BeFalse();
        var act = () => sut.GenerateExplanationAsync("title", "body", TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // The tests below reach the real method bodies through the SDK's HTTP layer: overriding the
    // virtual methods would replace the very extraction checks under test.

    [Fact]
    public async Task GenerateInsightsJson_ReturnsTheInsightsArrayFromTheToolUseBlock()
    {
        var sut = ClientReturning(
            """{"type":"tool_use","id":"tu_1","name":"record_insights","input":{"insights":[{"type":"summary","title":"t","body":"b","data":{}}]}}"""
        );

        var json = await sut.GenerateInsightsJsonAsync("prompt", TestContext.Current.CancellationToken);

        JsonNode.Parse(json)!.AsArray().Should().ContainSingle();
        json.Should().Contain("\"title\":\"t\"");
    }

    [Fact]
    public async Task GenerateInsightsJson_WhenNoToolUseBlock_Throws()
    {
        var sut = ClientReturning("""{"type":"text","text":"I could not produce insights."}""");

        var act = () => sut.GenerateInsightsJsonAsync("prompt", TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*expected tool use block*");
    }

    [Fact]
    public async Task GenerateInsightsJson_WhenToolUseHasAnotherName_Throws()
    {
        var sut = ClientReturning(
            """{"type":"tool_use","id":"tu_1","name":"something_else","input":{"insights":[]}}"""
        );

        var act = () => sut.GenerateInsightsJsonAsync("prompt", TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*expected tool use block*");
    }

    [Fact]
    public async Task GenerateInsightsJson_WhenInsightsArrayMissing_Throws()
    {
        var sut = ClientReturning("""{"type":"tool_use","id":"tu_1","name":"record_insights","input":{"other":1}}""");

        var act = () => sut.GenerateInsightsJsonAsync("prompt", TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*missing 'insights' array*");
    }

    [Fact]
    public async Task GenerateExplanation_ReturnsTheTextBlock()
    {
        var sut = ClientReturning("""{"type":"text","text":"1. Do the thing."}""");

        var text = await sut.GenerateExplanationAsync("title", "body", TestContext.Current.CancellationToken);

        text.Should().Be("1. Do the thing.");
    }

    [Fact]
    public async Task GenerateExplanation_WhenNoTextBlock_Throws()
    {
        var sut = ClientReturning(
            """{"type":"tool_use","id":"tu_1","name":"record_insights","input":{"insights":[]}}"""
        );

        var act = () => sut.GenerateExplanationAsync("title", "body", TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*no text*");
    }

    private static AnthropicIntelligenceClient ClientReturning(string contentBlockJson)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ANTHROPIC_API_KEY"] = "test-key" })
            .Build();
        var body =
            """{"id":"msg_1","type":"message","role":"assistant","model":"claude-sonnet-4-6","content":["""
            + contentBlockJson
            + """],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}""";
        var http = new HttpClient(new StubHandler(body));
        return new AnthropicIntelligenceClient(configuration, NullLogger<AnthropicIntelligenceClient>.Instance, http);
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                }
            );
    }
}
