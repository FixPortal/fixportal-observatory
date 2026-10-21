using AwesomeAssertions;
using Microsoft.Extensions.Configuration;

namespace AiObservatory.Api.Tests;

public class ActivityOptionsTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void ResolveProjectOwners_returns_empty_when_unset_or_blank()
    {
        ActivityOptions.ResolveProjectOwners(Config([])).Should().BeEmpty();
        ActivityOptions.ResolveProjectOwners(Config(new() { ["Activity:ProjectOwners"] = "  " })).Should().BeEmpty();
    }

    [Fact]
    public void ResolveProjectOwners_reads_the_delimited_scalar_shape()
    {
        var owners = ActivityOptions.ResolveProjectOwners(
            Config(new() { ["Activity:ProjectOwners"] = "FixPortal, fix-portal;other\norg" })
        );

        owners.Should().Equal("FixPortal", "fix-portal", "other", "org");
    }

    [Fact]
    public void ResolveProjectOwners_prefers_the_array_shape_when_present()
    {
        var owners = ActivityOptions.ResolveProjectOwners(
            Config(
                new()
                {
                    ["Activity:ProjectOwners:0"] = "FixPortal",
                    ["Activity:ProjectOwners:1"] = "fix-portal",
                    ["Activity:ProjectOwners"] = "scalar-ignored",
                }
            )
        );

        owners.Should().Equal("FixPortal", "fix-portal");
    }

    [Theory]
    // The VaultName/SecretName form: no '/', no whitespace, and ';' is in the split set — the
    // exact shape that used to survive as two bogus owners (M16).
    [InlineData("@Microsoft.KeyVault(VaultName=obs-vault;SecretName=ActivityOwners)")]
    // The SecretUri form was already discarded by the no-'/' rule; it must stay discarded.
    [InlineData("@Microsoft.KeyVault(SecretUri=https://obs-vault.vault.azure.net/secrets/ActivityOwners)")]
    public void ResolveProjectOwners_rejects_an_unresolved_keyvault_reference_as_allow_everything(string reference)
    {
        // App Service leaves the reference verbatim when the secret is unreadable. Registered
        // as owners, the fragments form a non-empty allowlist matching nothing — blank
        // Activity/GitHub tabs, and the negated disallowed-projects cleanup would match every
        // stored session. Empty means allow-everything, which fails open instead.
        var owners = ActivityOptions.ResolveProjectOwners(Config(new() { ["Activity:ProjectOwners"] = reference }));

        owners.Should().BeEmpty();
    }

    [Fact]
    public void ResolveProjectOwners_drops_a_keyvault_reference_element_from_the_array_shape()
    {
        var owners = ActivityOptions.ResolveProjectOwners(
            Config(
                new()
                {
                    ["Activity:ProjectOwners:0"] = "@Microsoft.KeyVault(VaultName=obs-vault;SecretName=ActivityOwners)",
                    ["Activity:ProjectOwners:1"] = "FixPortal",
                }
            )
        );

        owners.Should().Equal("FixPortal");
    }
}
