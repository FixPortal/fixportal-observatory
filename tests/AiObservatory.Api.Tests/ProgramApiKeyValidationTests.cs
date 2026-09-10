using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;

namespace AiObservatory.Api.Tests;

/// <summary>
/// Outside Development the three API keys are the only thing between the internet and the
/// admin surface, so startup refuses keys that are absent, placeholder, unresolved Key Vault
/// references — or too short to be unguessable. A12: before the length floor, a one-character
/// key passed every gate and stood as a bearer credential.
/// </summary>
public class ProgramApiKeyValidationTests
{
    private const string ValidAdminKey = "admin-key-0123456789abcdef";
    private const string ValidReadOnlyKey = "read-only-0123456789abcdef";
    private const string ValidIdeKey = "ide-key-0123456789abcdef012";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("change-me")]
    [InlineData("x")]
    [InlineData("0123456789abcde")]
    [InlineData("@Microsoft.KeyVault(VaultName=v;SecretName=s)")]
    public void RejectsAnAbsentDefaultUnresolvedOrGuessableAdminKey(string? adminKey)
    {
        var builder = Builder();
        builder.Configuration["OBSERVATORY_API_KEY"] = adminKey;

        var act = () => Program.ValidateApiKeys(builder);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AcceptsThreeDistinctLongKeys()
    {
        var builder = Builder();
        builder.Configuration["OBSERVATORY_API_KEY"] = ValidAdminKey;

        var act = () => Program.ValidateApiKeys(builder);

        act.Should().NotThrow();
    }

    private static WebApplicationBuilder Builder()
    {
        var builder = WebApplication.CreateBuilder(
            new WebApplicationOptions { EnvironmentName = Environments.Production }
        );
        builder.Configuration["OBSERVATORY_READONLY_API_KEY"] = ValidReadOnlyKey;
        builder.Configuration["OBSERVATORY_IDE_API_KEY"] = ValidIdeKey;
        return builder;
    }
}
