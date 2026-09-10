using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace AiObservatory.Api.Tests;

public sealed class IdeApiKeyEndpointFilterTests
{
    [Fact]
    public async Task AcceptsOnlyTheDedicatedHeader()
    {
        var config = Substitute.For<IConfiguration>();
        config["OBSERVATORY_IDE_API_KEY"].Returns("ide-key-12345");
        config["OBSERVATORY_API_KEY"].Returns("admin-key-12345");
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Production);
        var filter = new IdeApiKeyEndpointFilter(config, environment);
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Observatory-IDE-Key"] = "ide-key-12345";
        var called = false;

        var result = await filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(http),
            _ =>
            {
                called = true;
                return ValueTask.FromResult<object?>(Results.Ok());
            }
        );

        called.Should().BeTrue();
        result.Should().BeOfType<Ok>();
    }

    [Theory]
    [InlineData("X-Observatory-Key", "admin-key-12345")]
    [InlineData("X-Observatory-IDE-Key", "wrong-key-12345")]
    public async Task RejectsGeneralOrIncorrectKeys(string header, string value)
    {
        var config = Substitute.For<IConfiguration>();
        config["OBSERVATORY_IDE_API_KEY"].Returns("ide-key-12345");
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Production);
        var filter = new IdeApiKeyEndpointFilter(config, environment);
        var http = new DefaultHttpContext();
        http.Request.Headers[header] = value;

        var result = await filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(http),
            _ => ValueTask.FromResult<object?>(Results.Ok())
        );

        result.Should().BeOfType<UnauthorizedHttpResult>();
    }

    [Fact]
    public async Task DoesNotTreatHumanEntraAuthenticationAsIntegrationAuthorization()
    {
        var config = Substitute.For<IConfiguration>();
        config["OBSERVATORY_IDE_API_KEY"].Returns("ide-key-12345");
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Production);
        var filter = new IdeApiKeyEndpointFilter(config, environment);
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "chris")], "Bearer")),
        };

        var result = await filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(http),
            _ => ValueTask.FromResult<object?>(Results.Ok())
        );

        result.Should().BeOfType<UnauthorizedHttpResult>();
    }
}
