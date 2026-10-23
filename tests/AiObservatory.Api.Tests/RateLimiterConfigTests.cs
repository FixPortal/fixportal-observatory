using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AiObservatory.Api.Tests;

/// <summary>
/// The per-IP fixed-window limiter on the /api group. The integration factory raises the permit
/// limit to 100000, so nothing else reaches the 429; this runs the exact policy Program.cs
/// registers (Program.ConfigureApiRateLimiter) on a bare TestHost with a low limit and a
/// distinct peer address per simulated client.
/// </summary>
public class RateLimiterConfigTests
{
    private const int PermitLimit = 2;

    [Fact]
    public async Task Policy_WhenAClientExceedsThePermitLimit_Returns429()
    {
        using var host = await BuildHostAsync();
        var client = host.GetTestClient();

        (await GetAsync(client, "10.0.0.1")).Should().Be(HttpStatusCode.OK);
        (await GetAsync(client, "10.0.0.1")).Should().Be(HttpStatusCode.OK);
        (await GetAsync(client, "10.0.0.1")).Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Policy_PartitionsByPeerAddress_SoAnotherClientKeepsItsOwnBucket()
    {
        using var host = await BuildHostAsync();
        var client = host.GetTestClient();
        for (var i = 0; i < PermitLimit; i++)
        {
            await GetAsync(client, "10.0.0.1");
        }

        (await GetAsync(client, "10.0.0.1")).Should().Be(HttpStatusCode.TooManyRequests);
        (await GetAsync(client, "10.0.0.2")).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Policy_RotatingTheApiKeyHeader_DoesNotMintAFreshBucket()
    {
        // The limiter runs before the key filter, so the header is unvalidated here: keying on
        // it once let an anonymous caller bypass the limit by sending a random value each time.
        using var host = await BuildHostAsync();
        var client = host.GetTestClient();
        var statuses = new List<HttpStatusCode>();

        for (var i = 0; i < PermitLimit + 1; i++)
        {
            statuses.Add(await GetAsync(client, "10.0.0.1", apiKey: $"random-{i}"));
        }

        statuses[^1].Should().Be(HttpStatusCode.TooManyRequests);
    }

    private static async Task<HttpStatusCode> GetAsync(HttpClient client, string peer, string? apiKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/limited");
        request.Headers.Add("X-Test-Peer", peer);
        if (apiKey is not null)
        {
            request.Headers.Add("X-Observatory-Key", apiKey);
        }
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    private static async Task<IHost> BuildHostAsync()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { ["RateLimiting:ApiPermitLimit"] = PermitLimit.ToString() }
            )
            .Build();
        var builder = new HostBuilder().ConfigureWebHost(webBuilder =>
        {
            webBuilder
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddRateLimiter(o => Program.ConfigureApiRateLimiter(o, configuration));
                })
                .Configure(app =>
                {
                    // TestServer leaves RemoteIpAddress null, which would put every client in the
                    // single "anonymous" partition; set a distinct peer per simulated client.
                    app.Use(
                        async (ctx, next) =>
                        {
                            ctx.Connection.RemoteIpAddress = IPAddress.Parse(
                                ctx.Request.Headers["X-Test-Peer"].ToString()
                            );
                            await next();
                        }
                    );
                    app.UseRouting();
                    app.UseRateLimiter();
                    app.UseEndpoints(endpoints =>
                        endpoints.MapGet("/limited", () => Results.Ok()).RequireRateLimiting("api")
                    );
                });
        });

        return await builder.StartAsync(TestContext.Current.CancellationToken);
    }
}
