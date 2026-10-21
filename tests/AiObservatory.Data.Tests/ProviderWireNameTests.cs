using System.Text.Json;
using System.Text.Json.Serialization;
using AiObservatory.Data.Entities;
using AwesomeAssertions;

namespace AiObservatory.Data.Tests;

public sealed class ProviderWireNameTests
{
    // Mirrors the converter registered in AiObservatory.Api Program.cs; the Data project cannot reference the API.
    private static readonly JsonSerializerOptions ApiOptions = new()
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    [Theory]
    [InlineData(Provider.Zai, "zai")]
    [InlineData(Provider.DeepSeek, "deepseek")]
    [InlineData(Provider.OpenAI, "openai")]
    public void Provider_serialises_to_lowercase_wire_names(Provider provider, string wireName)
    {
        JsonSerializer.Serialize(provider, ApiOptions).Should().Be($"\"{wireName}\"");
        JsonSerializer.Deserialize<Provider>($"\"{wireName}\"", ApiOptions).Should().Be(provider);
    }
}
