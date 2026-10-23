using System.Net;
using System.Text.Json;
using AiObservatory.Api.Audit;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AiObservatory.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class OperationalInventoryEndpointsWafTests
{
    [Fact]
    public async Task DefaultDisabled_DoesNotExposeInventory()
    {
        await using var factory = new AiObservatoryApiFactory();
        await factory.InitializeAsync();
        using var client = factory.CreateAdminClient();
        using var response = await client.GetAsync(
            "/api/admin/operational-inventory",
            TestContext.Current.CancellationToken
        );
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task EnabledWithAuditDisabled_AdminCanReadInventory()
    {
        await using var factory = new AiObservatoryApiFactory();
        factory.ConfigurationOverrides["Audit:Enabled"] = "false";
        factory.ConfigurationOverrides["OperationalInventory:Enabled"] = "true";
        await factory.InitializeAsync();
        using var client = factory.CreateAdminClient();
        using var response = await client.GetAsync(
            "/api/admin/operational-inventory",
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
        json.RootElement.GetProperty("schemaVersion").GetInt32().Should().Be(1);
        json.RootElement.GetProperty("policy").GetProperty("storedRowPresent").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("runs").GetProperty("total").GetInt64().Should().Be(0);
        var options = factory.Services.GetRequiredService<IOptions<AuditOptions>>().Value;
        options.Enabled.Should().BeFalse();
        options.WorkerEnabled.Should().BeFalse();
    }
}
