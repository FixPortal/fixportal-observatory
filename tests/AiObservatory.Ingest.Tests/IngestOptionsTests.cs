using AwesomeAssertions;
using Microsoft.Extensions.Configuration;

namespace AiObservatory.Ingest.Tests;

/// <summary>
/// Covers <see cref="IngestOptions.ResolveGitHubRepoAllowlist"/>, which has to accept both the
/// array shape used locally and the single delimited value App Service produces from the
/// deployed Key Vault reference.
/// </summary>
public class IngestOptionsTests
{
    private const string Key = "Ingest:GitHubRepoAllowlist";

    private static IConfiguration Config(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    [Fact]
    public void ResolveGitHubRepoAllowlist_WhenBoundAsArray_ReturnsEveryEntry()
    {
        var cfg = Config(($"{Key}:0", "FixPortal/one"), ($"{Key}:1", "FixPortal/two"));

        IngestOptions.ResolveGitHubRepoAllowlist(cfg).Should().Equal("FixPortal/one", "FixPortal/two");
    }

    [Theory]
    [InlineData("FixPortal/one,FixPortal/two")]
    [InlineData("FixPortal/one; FixPortal/two")]
    [InlineData("FixPortal/one\nFixPortal/two")]
    [InlineData("  FixPortal/one ,, FixPortal/two  ")]
    public void ResolveGitHubRepoAllowlist_WhenBoundAsDelimitedScalar_SplitsAndTrims(string value)
    {
        IngestOptions.ResolveGitHubRepoAllowlist(Config((Key, value))).Should().Equal("FixPortal/one", "FixPortal/two");
    }

    [Fact]
    public void ResolveGitHubRepoAllowlist_WhenScalarRepeatsARepo_DeduplicatesCaseInsensitively()
    {
        IngestOptions
            .ResolveGitHubRepoAllowlist(Config((Key, "FixPortal/one,fixportal/ONE")))
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be("FixPortal/one");
    }

    // App Service leaves an unresolvable Key Vault reference in place as a literal string.
    // Splitting it on ',' must not produce a repo the GitHub client then 404s on hourly.
    // Both reference forms are covered: VaultName/SecretName has no '/' at all, SecretUri
    // has several — neither yields exactly two segments.
    [Theory]
    [InlineData("@Microsoft.KeyVault(VaultName=kv;SecretName=github-repo-allowlist)")]
    [InlineData("@Microsoft.KeyVault(SecretUri=https://kv.vault.azure.net/secrets/github-repo-allowlist/)")]
    [InlineData("not-an-owner-repo")]
    [InlineData("too/many/segments")]
    [InlineData("/leading-slash")]
    [InlineData("trailing-slash/")]
    [InlineData("FixPortal /one")]
    [InlineData("FixPortal/ one")]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveGitHubRepoAllowlist_WhenValueIsNotOwnerRepo_ReturnsEmpty(string value)
    {
        IngestOptions.ResolveGitHubRepoAllowlist(Config((Key, value))).Should().BeEmpty();
    }

    [Fact]
    public void ResolveGitHubRepoAllowlist_WhenKeyIsAbsent_ReturnsEmpty()
    {
        IngestOptions.ResolveGitHubRepoAllowlist(Config()).Should().BeEmpty();
    }

    [Fact]
    public void ResolveGitHubRepoAllowlist_WhenScalarMixesValidAndInvalid_KeepsOnlyTheValid()
    {
        IngestOptions
            .ResolveGitHubRepoAllowlist(Config((Key, "FixPortal/one,garbage,FixPortal/two")))
            .Should()
            .Equal("FixPortal/one", "FixPortal/two");
    }

    [Fact]
    public void BindGoogleCloudCatalogReadsTheDeploymentEnvironmentKeys()
    {
        var cfg = Config(
            ("GOOGLE_CLOUD_CATALOG_API_KEY", "catalog-key"),
            ("GOOGLE_CLOUD_CATALOG_SERVICE_ID", "service-id")
        );
        var options = new IngestOptions();

        IngestOptions.BindGoogleCloudCatalog(cfg, options);

        options.GoogleCloudCatalogApiKey.Should().Be("catalog-key");
        options.GoogleCloudCatalogServiceId.Should().Be("service-id");
    }
}
