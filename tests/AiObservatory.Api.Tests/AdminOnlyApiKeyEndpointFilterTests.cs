using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace AiObservatory.Api.Tests;

public class AdminOnlyApiKeyEndpointFilterTests
{
    private readonly IConfiguration _config = Substitute.For<IConfiguration>();
    private readonly IHostEnvironment _env = Substitute.For<IHostEnvironment>();
    private readonly AdminOnlyApiKeyEndpointFilter _sut;

    public AdminOnlyApiKeyEndpointFilterTests()
    {
        _env.EnvironmentName.Returns(Environments.Production);
        _sut = new AdminOnlyApiKeyEndpointFilter(_config, _env);
    }

    private static EndpointFilterInvocationContext BuildContext(
        string method,
        string? key = null,
        bool authenticated = false
    )
    {
        var httpContext = new DefaultHttpContext();
        if (authenticated)
        {
            httpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.Name, "chris")], authenticationType: "Bearer")
            );
        }
        httpContext.Request.Method = method;
        if (key is not null)
        {
            httpContext.Request.Headers["X-Observatory-Key"] = key;
        }
        return EndpointFilterInvocationContext.Create(httpContext);
    }

    [Fact]
    public async Task InvokeAsync_WhenAdminKeyConfigured_AllowsValidAdminKey()
    {
        _config["OBSERVATORY_API_KEY"].Returns("admin-key-12345");
        var context = BuildContext("GET", "admin-key-12345");

        var nextCalled = false;
        ValueTask<object?> Next(EndpointFilterInvocationContext _)
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        }

        var result = await _sut.InvokeAsync(context, Next);

        nextCalled.Should().BeTrue();
        result.Should().BeOfType<Ok>();
    }

    [Fact]
    public async Task InvokeAsync_WhenAdminKeyConfigured_RejectsReadonlyKey()
    {
        // The whole point of this filter: a valid readonly key must NOT pass here,
        // even though the readonly key is accepted by the shared ApiKeyEndpointFilter
        // on every other GET route.
        _config["OBSERVATORY_API_KEY"].Returns("admin-key-12345");
        var context = BuildContext("GET", "readonly-key-12345");

        var nextCalled = false;
        ValueTask<object?> Next(EndpointFilterInvocationContext _)
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        }

        var result = await _sut.InvokeAsync(context, Next);

        nextCalled.Should().BeFalse();
        result.Should().BeOfType<UnauthorizedHttpResult>();
    }

    [Fact]
    public async Task InvokeAsync_WhenNoKeyHeader_RejectsAnonymousRequest()
    {
        _config["OBSERVATORY_API_KEY"].Returns("admin-key-12345");
        var context = BuildContext("GET");

        var nextCalled = false;
        ValueTask<object?> Next(EndpointFilterInvocationContext _)
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        }

        var result = await _sut.InvokeAsync(context, Next);

        nextCalled.Should().BeFalse();
        result.Should().BeOfType<UnauthorizedHttpResult>();
    }

    [Fact]
    public async Task InvokeAsync_WhenEntraAuthenticated_AllowsRegardlessOfKey()
    {
        _config["OBSERVATORY_API_KEY"].Returns("admin-key-12345");
        var context = BuildContext("GET", authenticated: true);

        var nextCalled = false;
        ValueTask<object?> Next(EndpointFilterInvocationContext _)
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        }

        var result = await _sut.InvokeAsync(context, Next);

        nextCalled.Should().BeTrue();
        result.Should().BeOfType<Ok>();
    }

    [Theory]
    [InlineData("Development", true, 200)]
    [InlineData("Production", false, 503)]
    public async Task InvokeAsync_WhenAdminKeyNotConfigured_FailsClosedOutsideDev(
        string environment,
        bool expectNext,
        int expectedStatus
    )
    {
        _env.EnvironmentName.Returns(environment);
        _config["OBSERVATORY_API_KEY"].Returns((string?)null);
        var context = BuildContext("GET");

        var nextCalled = false;
        ValueTask<object?> Next(EndpointFilterInvocationContext _)
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        }

        var result = await _sut.InvokeAsync(context, Next);

        nextCalled.Should().Be(expectNext);
        if (expectNext)
        {
            result.Should().BeOfType<Ok>();
        }
        else
        {
            var statusResult = result.Should().BeOfType<StatusCodeHttpResult>().Subject;
            statusResult.StatusCode.Should().Be(expectedStatus);
        }
    }
}
