using AiObservatory.Api.Services.GitHub;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AiObservatory.Api.Tests.Services;

public sealed class GitHubBillingRegistrationTests
{
    [Theory]
    [InlineData(null, "my-org")]
    [InlineData("", "my-org")]
    [InlineData("   ", "my-org")]
    [InlineData("token", null)]
    [InlineData("token", "  ")]
    [InlineData("@Microsoft.KeyVault(VaultName=kv;SecretName=github-token)", "my-org")]
    public void StaysOffForMissingBlankOrUnresolvedConfiguration(string? token, string? org)
    {
        var services = new ServiceCollection();

        services.AddGitHubBilling(Config(token, org));

        services.Should().NotContain(d => d.ServiceType == typeof(GitHubBillingSyncService));
    }

    [Fact]
    public void RegistersTheArmWhenBothValuesAreGenuinelyConfigured()
    {
        var services = new ServiceCollection();

        services.AddGitHubBilling(Config("token", "my-org"));

        services.Should().Contain(d => d.ServiceType == typeof(GitHubBillingSyncService));
    }

    private static IConfiguration Config(string? token, string? org) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { ["GITHUB_TOKEN"] = token, ["GITHUB_BILLING_ORG"] = org }
            )
            .Build();
}
