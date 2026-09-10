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
}
