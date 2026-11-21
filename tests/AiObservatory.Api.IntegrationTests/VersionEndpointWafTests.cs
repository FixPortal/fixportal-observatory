using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using AwesomeAssertions;

namespace AiObservatory.Api.IntegrationTests;

[Trait("Category", "Integration")]
[Collection("ApiFactory")]
public sealed class VersionEndpointWafTests(AiObservatoryApiFactory factory)
{
    [Fact]
    public async Task Version_reports_the_commit_the_build_was_made_from_without_a_key()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateAnonymousClient();

        using var response = await client.GetAsync("/version", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var informational = typeof(Program)
            .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion;
        // The SDK appends "+<commit>" from git, which is what the deploy probe compares with github.sha.
        informational.Should().MatchRegex(@"\+[0-9a-f]{40}$");
        body.GetProperty("commit").GetString().Should().Be(informational[(informational.IndexOf('+') + 1)..]);
    }
}
