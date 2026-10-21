using System.Net;
using AwesomeAssertions;

namespace AiObservatory.Api.IntegrationTests;

public sealed class IdeEndpointAuthorizationTests
{
    [Trait("Category", "Integration")]
    [Fact]
    public async Task DedicatedKeyCanReadOnlyTheIdeSurface()
    {
        await using var factory = new AiObservatoryApiFactory();
        await factory.InitializeAsync();
        using var client = factory.CreateIdeClient();

        var snapshot = await client.GetAsync("/api/ide/v1/routing-snapshot", TestContext.Current.CancellationToken);
        var general = await client.GetAsync("/api/aggregates", TestContext.Current.CancellationToken);

        snapshot.StatusCode.Should().Be(HttpStatusCode.OK);
        general.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Trait("Category", "Integration")]
    [Fact]
    public async Task GeneralKeysCannotReadTheIdeSurface()
    {
        await using var factory = new AiObservatoryApiFactory();
        await factory.InitializeAsync();
        using var admin = factory.CreateAdminClient();
        using var readOnly = factory.CreateReadOnlyClient();

        var adminResponse = await admin.GetAsync("/api/ide/v1/routing-snapshot", TestContext.Current.CancellationToken);
        var readOnlyResponse = await readOnly.GetAsync(
            "/api/ide/v1/routing-snapshot",
            TestContext.Current.CancellationToken
        );

        adminResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        readOnlyResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
