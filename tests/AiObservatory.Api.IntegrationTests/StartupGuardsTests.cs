using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.Hosting;

namespace AiObservatory.Api.IntegrationTests;

/// <summary>
/// Composition-root startup guards in Program.cs — each is a documented fix for a real
/// past incident (a guessable "change-me" admin key reachable outside dev; a missing
/// DB_CONNECTION failing silently at first request instead of at boot; the dev-only
/// /api/dev/seed route being reachable in Production). Every test here uses its own
/// throwaway factory (never the shared collection fixture) because it needs a
/// non-default Environment/ApiKeyOverride.
/// </summary>
public class StartupGuardsTests
{
    private const string KeyVaultReference = "@Microsoft.KeyVault(VaultName=fpaiobs-kv;SecretName=observatory-api-key)";

    [Theory]
    [InlineData(null)]
    [InlineData("change-me")]
    [InlineData(KeyVaultReference)]
    public async Task Startup_WhenApiKeyIsUnsetOrPlaceholder_ThrowsOutsideDevelopment(string? apiKey)
    {
        await using var factory = new AiObservatoryApiFactory
        {
            Environment = Environments.Production,
            ApiKeyOverride = apiKey,
        };

        var thrown = CaptureServicesException(factory);

        thrown.Should().NotBeNull();
        ExceptionChainContains(thrown, "OBSERVATORY_API_KEY")
            .Should()
            .BeTrue($"the exception chain should mention OBSERVATORY_API_KEY; got: {thrown}");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("change-me")]
    [InlineData(KeyVaultReference)]
    public async Task Startup_WhenReadOnlyApiKeyIsUnsetOrPlaceholder_ThrowsOutsideDevelopment(string? apiKey)
    {
        await using var factory = new AiObservatoryApiFactory
        {
            Environment = Environments.Production,
            ReadOnlyKeyOverride = apiKey,
        };

        var thrown = CaptureServicesException(factory);

        thrown.Should().NotBeNull();
        ExceptionChainContains(thrown, "OBSERVATORY_READONLY_API_KEY")
            .Should()
            .BeTrue($"the exception chain should mention OBSERVATORY_READONLY_API_KEY; got: {thrown}");
    }

    [Fact]
    public async Task Startup_WhenAdminAndReadOnlyKeysMatch_ThrowsOutsideDevelopment()
    {
        const string sharedKey = "same-production-key";
        await using var factory = new AiObservatoryApiFactory
        {
            Environment = Environments.Production,
            ApiKeyOverride = sharedKey,
            ReadOnlyKeyOverride = sharedKey,
        };

        var thrown = CaptureServicesException(factory);

        thrown.Should().NotBeNull();
        ExceptionChainContains(thrown, "must be different")
            .Should()
            .BeTrue($"the exception chain should reject identical API keys; got: {thrown}");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("change-me")]
    [InlineData(KeyVaultReference)]
    [InlineData(" padded-key ")]
    public async Task Startup_WhenIdeKeyIsInvalid_ThrowsOutsideDevelopment(string? ideKey)
    {
        await using var factory = new AiObservatoryApiFactory
        {
            Environment = Environments.Production,
            IdeKeyOverride = ideKey,
        };

        var thrown = CaptureServicesException(factory);

        thrown.Should().NotBeNull();
        ExceptionChainContains(thrown, "OBSERVATORY_IDE_API_KEY").Should().BeTrue();
    }

    [Theory]
    [InlineData(AiObservatoryApiFactory.AdminKey)]
    [InlineData(AiObservatoryApiFactory.ReadOnlyKey)]
    public async Task Startup_WhenIdeKeyDuplicatesAnExistingKey_ThrowsOutsideDevelopment(string ideKey)
    {
        await using var factory = new AiObservatoryApiFactory
        {
            Environment = Environments.Production,
            IdeKeyOverride = ideKey,
        };

        var thrown = CaptureServicesException(factory);

        thrown.Should().NotBeNull();
        ExceptionChainContains(thrown, "must be different").Should().BeTrue();
    }

    [Trait("Category", "Integration")]
    [Fact]
    public async Task Startup_WhenApiKeyIsPlaceholder_SucceedsInDevelopment()
    {
        await using var factory = new AiObservatoryApiFactory
        {
            Environment = Environments.Development,
            ApiKeyOverride = "change-me",
        };

        factory.Services.Should().NotBeNull();
    }

    [Fact]
    public async Task Startup_WhenDbConnectionUnset_ThrowsRegardlessOfEnvironment()
    {
        await using var factory = new AiObservatoryApiFactory { Environment = Environments.Development };
        factory.SetDbConnection(null);

        var thrown = CaptureServicesException(factory);

        thrown.Should().NotBeNull();
        ExceptionChainContains(thrown, "DB_CONNECTION")
            .Should()
            .BeTrue($"the exception chain should mention DB_CONNECTION; got: {thrown}");
    }

    /// <summary>Walks Exception/InnerException (and AggregateException.InnerExceptions) looking
    /// for any message containing <paramref name="fragment"/> — the exact wrapper type
    /// HostFactoryResolver uses to surface a Program.cs startup throw isn't a stable contract
    /// to assert against; the message content is.</summary>
    private static bool ExceptionChainContains(Exception ex, string fragment)
    {
        if (ex.Message.Contains(fragment, StringComparison.Ordinal))
        {
            return true;
        }
        if (ex is AggregateException agg)
        {
            return agg.InnerExceptions.Any(e => ExceptionChainContains(e, fragment));
        }
        return ex.InnerException is not null && ExceptionChainContains(ex.InnerException, fragment);
    }

    private static Exception? CaptureServicesException(AiObservatoryApiFactory factory) =>
        Record.Exception(() => factory.Services);

    [Trait("Category", "Integration")]
    [Fact]
    public async Task DevSeedRoute_Returns404InProduction()
    {
        await using var factory = new AiObservatoryApiFactory { Environment = Environments.Production };
        await factory.InitializeAsync();
        using var client = factory.CreateAdminClient();

        var response = await client.PostAsync("/api/dev/seed", content: null, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Trait("Category", "Integration")]
    [Fact]
    public async Task DevSeedRoute_IsReachableInDevelopment()
    {
        await using var factory = new AiObservatoryApiFactory { Environment = Environments.Development };
        await factory.InitializeAsync();
        using var client = factory.CreateAdminClient();

        var response = await client.PostAsync("/api/dev/seed", content: null, TestContext.Current.CancellationToken);

        response.StatusCode.Should().NotBe(HttpStatusCode.NotFound);
    }
}
