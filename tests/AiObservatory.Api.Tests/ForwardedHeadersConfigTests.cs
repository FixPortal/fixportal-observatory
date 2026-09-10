using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

namespace AiObservatory.Api.Tests;

/// <summary>
/// The ForwardedHeaders trust boundary — a prior rate-limiter-collapse incident: honouring
/// X-Forwarded-For from ANY caller let a public client spoof its rate-limit partition key
/// by rotating a fake header. Program.ConfigureForwardedHeaders (extracted, behaviour-
/// unchanged, from an inline lambda so it's independently testable) restricts trust to
/// RFC1918 private networks (the nginx sidecar in the Docker self-host topology).
/// </summary>
/// <remarks>
/// Uses a bare TestHost pipeline running the exact same ForwardedHeadersOptions
/// configuration as Program.cs, rather than the full WebApplicationFactory — this guard is
/// pure ASP.NET Core middleware behaviour, not something that needs Postgres or the rest of
/// the composition root to exercise.
/// </remarks>
public class ForwardedHeadersConfigTests
{
    [Fact]
    public void ConfigureForwardedHeaders_TrustsOnlyPrivateNetworks()
    {
        var options = new ForwardedHeadersOptions();

        Program.ConfigureForwardedHeaders(options);

        options.ForwardedHeaders.Should().Be(ForwardedHeaders.XForwardedFor);
        options.ForwardLimit.Should().Be(1);
        options.KnownProxies.Should().BeEmpty();
        options.KnownIPNetworks.Should().HaveCount(3);
    }

    [Fact]
    public async Task Pipeline_WhenRemoteIpIsPublic_IgnoresSpoofedForwardedForHeader()
    {
        // A public, untrusted "connecting" address — e.g. a caller reaching the API
        // directly, bypassing the nginx sidecar.
        using var host = await BuildTestHostAsync(remoteIp: System.Net.IPAddress.Parse("8.8.8.8"));
        var client = host.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("X-Forwarded-For", "1.2.3.4"); // attacker-controlled

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var observedIp = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // 8.8.8.8 is not in KnownIPNetworks: the spoofed header must be ignored and the
        // real connecting address kept — NOT the attacker-supplied 1.2.3.4.
        observedIp.Should().Be("8.8.8.8");
    }

    [Fact]
    public async Task Pipeline_WhenRemoteIpIsInTrustedPrivateNetwork_HonoursForwardedForHeader()
    {
        using var host = await BuildTestHostAsync(remoteIp: System.Net.IPAddress.Parse("10.0.5.7"));
        var client = host.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("X-Forwarded-For", "203.0.113.9"); // real client IP, forwarded by the trusted nginx sidecar

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var observedIp = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        observedIp.Should().Be("203.0.113.9");
    }

    private static async Task<IHost> BuildTestHostAsync(System.Net.IPAddress remoteIp)
    {
        var builder = new HostBuilder().ConfigureWebHost(webBuilder =>
        {
            webBuilder
                .UseTestServer()
                .Configure(app =>
                {
                    // Simulate the request arriving from a specific "connecting" peer
                    // (nginx sidecar vs. a direct public caller) before ForwardedHeaders runs.
                    app.Use(
                        async (ctx, next) =>
                        {
                            ctx.Connection.RemoteIpAddress = remoteIp;
                            await next();
                        }
                    );

                    app.UseForwardedHeaders(Options(Program.ConfigureForwardedHeaders));

                    app.Run(ctx => ctx.Response.WriteAsync(ctx.Connection.RemoteIpAddress?.ToString() ?? ""));
                });
        });

        return await builder.StartAsync(TestContext.Current.CancellationToken);
    }

    private static ForwardedHeadersOptions Options(Action<ForwardedHeadersOptions> configure)
    {
        var options = new ForwardedHeadersOptions();
        configure(options);
        return options;
    }
}
