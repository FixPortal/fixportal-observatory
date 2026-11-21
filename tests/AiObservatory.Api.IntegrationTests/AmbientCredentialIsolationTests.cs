using AiObservatory.Api.Services.Intelligence;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AiObservatory.Api.IntegrationTests;

/// <summary>
/// A factory host must not inherit a credential from the developer's environment, and must not
/// run the intelligence worker that would use one. The values below are sentinels the test
/// plants itself; the assertions check presence or absence only, never a value.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AmbientCredentialIsolationTests
{
    private static readonly string[] CredentialVariables = ["ANTHROPIC_API_KEY", "GITHUB_TOKEN", "GITHUB_BILLING_ORG"];

    [Fact]
    public async Task A_factory_host_ignores_ambient_credentials_and_does_not_run_the_intelligence_worker()
    {
        var original = CredentialVariables.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var name in CredentialVariables)
            {
                Environment.SetEnvironmentVariable(name, "ambient-sentinel-not-a-credential");
            }

            await using var factory = new AiObservatoryApiFactory();
            await factory.InitializeAsync();

            var configuration = factory.Services.GetRequiredService<IConfiguration>();
            foreach (var name in CredentialVariables)
            {
                configuration[name].Should().BeNull($"{name} must not reach a test host from the environment");
            }
            factory
                .Services.GetRequiredService<AnthropicIntelligenceClient>()
                .IsConfigured.Should()
                .BeFalse("a test host must not be able to call a real model provider");
            factory
                .Services.GetServices<IHostedService>()
                .OfType<IntelligenceWorkerService>()
                .Should()
                .BeEmpty("the worker races tests that drive their own worker and calls providers");
        }
        finally
        {
            foreach (var (name, value) in original)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}
