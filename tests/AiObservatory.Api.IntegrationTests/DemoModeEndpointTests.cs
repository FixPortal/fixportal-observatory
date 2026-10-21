using System.Net;
using AiObservatory.Data;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AiObservatory.Api.IntegrationTests;

/// <summary>
/// Demo mode runs the real composition root in Production with the demo flag on: the seed and
/// reset routes exist, nothing else can write, and without the flag none of that exists.
/// </summary>
[Trait("Category", "Integration")]
public class DemoModeEndpointTests
{
    private static AiObservatoryApiFactory DemoFactory() =>
        new() { Environment = Environments.Production, DemoMode = true };

    [Fact]
    public async Task Reset_with_the_admin_key_seeds_an_empty_demo_database()
    {
        await using var factory = DemoFactory();
        await factory.InitializeAsync();
        using var client = factory.CreateAdminClient();

        var response = await client.PostAsync(
            "/api/dev/reset-demo",
            content: null,
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        (await db.DailyAggregates.AnyAsync(TestContext.Current.CancellationToken)).Should().BeTrue();
    }

    [Theory]
    [InlineData("/api/dev/reset-demo/")]
    [InlineData("/API/DEV/RESET-DEMO")]
    public async Task Reset_is_reachable_through_trailing_slash_and_case_variants(string path)
    {
        // Routing matches these to the reset endpoint, so the write block must not turn them into
        // a misleading "read-only" 403 (a DEMO_API_URL variable ending in a slash would do it).
        await using var factory = DemoFactory();
        await factory.InitializeAsync();
        using var client = factory.CreateAdminClient();

        var response = await client.PostAsync(path, content: null, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Reset_with_the_read_only_key_is_rejected()
    {
        await using var factory = DemoFactory();
        await factory.InitializeAsync();
        using var client = factory.CreateReadOnlyClient();

        var response = await client.PostAsync(
            "/api/dev/reset-demo",
            content: null,
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("POST", "/api/subscriptions")]
    [InlineData("PUT", "/api/subscriptions/00000000-0000-0000-0000-000000000001")]
    [InlineData("PATCH", "/api/subscriptions/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "/api/subscriptions/00000000-0000-0000-0000-000000000001")]
    public async Task Writes_are_refused_even_with_the_admin_key(string method, string path)
    {
        await using var factory = DemoFactory();
        await factory.InitializeAsync();
        using var client = factory.CreateAdminClient();

        var response = await client.SendAsync(
            new HttpRequestMessage(new HttpMethod(method), path),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_IDE_route_group_is_blocked_too()
    {
        await using var factory = DemoFactory();
        await factory.InitializeAsync();
        using var client = factory.CreateIdeClient();

        var response = await client.PostAsync(
            "/api/ide/v1/events",
            content: null,
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Reads_still_work_with_the_read_only_key()
    {
        await using var factory = DemoFactory();
        await factory.InitializeAsync();
        using (var admin = factory.CreateAdminClient())
        {
            (
                await admin.PostAsync("/api/dev/reset-demo", content: null, TestContext.Current.CancellationToken)
            ).EnsureSuccessStatusCode();
        }
        using var client = factory.CreateReadOnlyClient();

        var response = await client.GetAsync("/api/aggregates", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CORS_preflight_is_not_blocked()
    {
        await using var factory = DemoFactory();
        await factory.InitializeAsync();
        using var client = factory.CreateAnonymousClient();
        var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/aggregates");
        preflight.Headers.Add("Origin", "https://example.test");
        preflight.Headers.Add("Access-Control-Request-Method", "GET");
        preflight.Headers.Add("Access-Control-Request-Headers", "x-observatory-key");

        var response = await client.SendAsync(preflight, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Without_demo_mode_the_reset_route_does_not_exist_in_Production()
    {
        await using var factory = new AiObservatoryApiFactory { Environment = Environments.Production };
        await factory.InitializeAsync();
        using var client = factory.CreateAdminClient();

        var response = await client.PostAsync(
            "/api/dev/reset-demo",
            content: null,
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
