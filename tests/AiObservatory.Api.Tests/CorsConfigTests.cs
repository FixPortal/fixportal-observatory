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
/// The CORS default policy allows exactly one origin (SWA_ORIGIN). Runs the policy Program.cs
/// registers (Program.ConfigureCors) on a bare TestHost.
/// </summary>
public class CorsConfigTests
{
    private const string AllowedOrigin = "https://app.example.test";

    [Fact]
    public async Task Policy_EchoesTheConfiguredOrigin()
    {
        using var host = await BuildHostAsync();

        var response = await GetWithOriginAsync(host, AllowedOrigin);

        response
            .Headers.GetValues("Access-Control-Allow-Origin")
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(AllowedOrigin);
    }

    [Fact]
    public async Task Policy_OmitsTheAllowHeaderForAForeignOrigin()
    {
        using var host = await BuildHostAsync();

        var response = await GetWithOriginAsync(host, "https://evil.example.test");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    private static async Task<HttpResponseMessage> GetWithOriginAsync(IHost host, string origin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("Origin", origin);
        return await host.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<IHost> BuildHostAsync()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["SWA_ORIGIN"] = AllowedOrigin })
            .Build();
        var builder = new HostBuilder().ConfigureWebHost(webBuilder =>
        {
            webBuilder
                .UseTestServer()
                .ConfigureServices(services => services.AddCors(o => Program.ConfigureCors(o, configuration)))
                .Configure(app =>
                {
                    app.UseCors();
                    app.Run(ctx => ctx.Response.WriteAsync("ok"));
                });
        });

        return await builder.StartAsync(TestContext.Current.CancellationToken);
    }
}
