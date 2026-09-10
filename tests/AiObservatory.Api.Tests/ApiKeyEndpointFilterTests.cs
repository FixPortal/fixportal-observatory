using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace AiObservatory.Api.Tests;

public class ApiKeyEndpointFilterTests
{
    private readonly IConfiguration _config = Substitute.For<IConfiguration>();
    private readonly IHostEnvironment _env = Substitute.For<IHostEnvironment>();
    private readonly ApiKeyEndpointFilter _sut;

    public ApiKeyEndpointFilterTests()
    {
        // Default to a non-dev environment; the keyless-GET test overrides this per case.
        _env.EnvironmentName.Returns(Environments.Production);
        _sut = new ApiKeyEndpointFilter(_config, _env);
    }

    [Theory]
    // Keyless requests are allowed only in Development (reads and writes); any other
    // environment fails closed (503).
    [InlineData("GET", "Development", true, 200)]
    [InlineData("GET", "Production", false, 503)]
    [InlineData("POST", "Development", true, 200)]
    [InlineData("POST", "Production", false, 503)]
    public async Task InvokeAsync_WhenKeysNotConfigured_FailsClosedOutsideDev(
        string method,
        string environment,
        bool expectNext,
        int expectedStatus
    )
    {
        // Arrange
        _env.EnvironmentName.Returns(environment);
        _config["OBSERVATORY_API_KEY"].Returns((string?)null);
        _config["OBSERVATORY_READONLY_API_KEY"].Returns((string?)null);

        var httpContext = CreateHttpContext(method);
        var context = EndpointFilterInvocationContext.Create(httpContext);

        var nextCalled = false;
        ValueTask<object?> Next(EndpointFilterInvocationContext _)
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        }

        // Act
        var result = await _sut.InvokeAsync(context, Next);

        // Assert
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

    [Fact]
    public async Task InvokeAsync_WhenGetAndReadOnlyKeyUnconfigured_AllowsValidAdminKey()
    {
        // Arrange — OBSERVATORY_READONLY_API_KEY unset, OBSERVATORY_API_KEY set, Production.
        // A valid admin key on a GET must still be reachable even though the readonly
        // key was never configured (e.g. the Activity endpoints in a deploy that hasn't
        // set OBSERVATORY_READONLY_API_KEY).
        _config["OBSERVATORY_API_KEY"].Returns("admin-key-12345");
        _config["OBSERVATORY_READONLY_API_KEY"].Returns((string?)null);

        var httpContext = CreateHttpContext("GET", "admin-key-12345");
        var context = EndpointFilterInvocationContext.Create(httpContext);

        var nextCalled = false;
        ValueTask<object?> Next(EndpointFilterInvocationContext _)
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        }

        // Act
        var result = await _sut.InvokeAsync(context, Next);

        // Assert
        nextCalled.Should().BeTrue();
        result.Should().BeOfType<Ok>();
    }

    [Fact]
    public async Task InvokeAsync_WhenGetAndReadOnlyKeyUnconfigured_RejectsWrongKey()
    {
        // Arrange — same setup, but the header key doesn't match the admin key. Because
        // an admin key IS configured, a mismatch is a plain 401, not the 503 misconfig
        // fallback (that fallback only fires when no key is configured at all).
        _config["OBSERVATORY_API_KEY"].Returns("admin-key-12345");
        _config["OBSERVATORY_READONLY_API_KEY"].Returns((string?)null);

        var httpContext = CreateHttpContext("GET", "wrong-key-12345");
        var context = EndpointFilterInvocationContext.Create(httpContext);

        var nextCalled = false;
        ValueTask<object?> Next(EndpointFilterInvocationContext _)
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        }

        // Act
        var result = await _sut.InvokeAsync(context, Next);

        // Assert
        nextCalled.Should().BeFalse();
        result.Should().BeOfType<UnauthorizedHttpResult>();
    }

    [Fact]
    public async Task InvokeAsync_WhenGetAndReadOnlyKeyConfigured_AllowsValidReadOnlyKey()
    {
        // Arrange
        _config["OBSERVATORY_API_KEY"].Returns("admin-key-12345");
        _config["OBSERVATORY_READONLY_API_KEY"].Returns("readonly-key-12345");

        var httpContext = CreateHttpContext("GET", "readonly-key-12345");
        var context = EndpointFilterInvocationContext.Create(httpContext);

        var nextCalled = false;
        ValueTask<object?> Next(EndpointFilterInvocationContext _)
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        }

        // Act
        var result = await _sut.InvokeAsync(context, Next);

        // Assert
        nextCalled.Should().BeTrue();
        result.Should().BeOfType<Ok>();
    }

    [Fact]
    public async Task InvokeAsync_WhenGetAndReadOnlyKeyConfigured_AllowsValidAdminKey()
    {
        // Arrange
        _config["OBSERVATORY_API_KEY"].Returns("admin-key-12345");
        _config["OBSERVATORY_READONLY_API_KEY"].Returns("readonly-key-12345");

        var httpContext = CreateHttpContext("GET", "admin-key-12345");
        var context = EndpointFilterInvocationContext.Create(httpContext);

        var nextCalled = false;
        ValueTask<object?> Next(EndpointFilterInvocationContext _)
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        }

        // Act
        var result = await _sut.InvokeAsync(context, Next);

        // Assert
        nextCalled.Should().BeTrue();
        result.Should().BeOfType<Ok>();
    }

    [Fact]
    public async Task InvokeAsync_WhenGetAndReadOnlyKeyConfigured_RejectsInvalidKey()
    {
        // Arrange
        _config["OBSERVATORY_API_KEY"].Returns("admin-key-12345");
        _config["OBSERVATORY_READONLY_API_KEY"].Returns("readonly-key-12345");

        var httpContext = CreateHttpContext("GET", "wrong-key-12345");
        var context = EndpointFilterInvocationContext.Create(httpContext);

        var nextCalled = false;
        ValueTask<object?> Next(EndpointFilterInvocationContext _)
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        }

        // Act
        var result = await _sut.InvokeAsync(context, Next);

        // Assert
        nextCalled.Should().BeFalse();
        result.Should().BeOfType<UnauthorizedHttpResult>();
    }

    [Fact]
    public async Task InvokeAsync_WhenGetAndReadOnlyKeyConfigured_RejectsWrongLengthKey()
    {
        // Arrange
        _config["OBSERVATORY_API_KEY"].Returns("admin-key-12345");
        _config["OBSERVATORY_READONLY_API_KEY"].Returns("readonly-key-12345");

        var httpContext = CreateHttpContext("GET", "short"); // wrong length
        var context = EndpointFilterInvocationContext.Create(httpContext);

        var nextCalled = false;
        ValueTask<object?> Next(EndpointFilterInvocationContext _)
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        }

        // Act
        var result = await _sut.InvokeAsync(context, Next);

        // Assert
        nextCalled.Should().BeFalse();
        result.Should().BeOfType<UnauthorizedHttpResult>();
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("DELETE")]
    public async Task InvokeAsync_WhenUserAuthenticated_AllowsRegardlessOfKeys(string method)
    {
        // Arrange — keys configured, but no key header supplied. An Entra-authenticated
        // user (JWT bearer) must be allowed through for both reads and writes.
        _config["OBSERVATORY_API_KEY"].Returns("admin-key-12345");
        _config["OBSERVATORY_READONLY_API_KEY"].Returns("readonly-key-12345");

        var httpContext = CreateHttpContext(
            method,
            user: new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.Name, "chris")], authenticationType: "Bearer")
            )
        );
        var context = EndpointFilterInvocationContext.Create(httpContext);

        var nextCalled = false;
        ValueTask<object?> Next(EndpointFilterInvocationContext _)
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        }

        // Act
        var result = await _sut.InvokeAsync(context, Next);

        // Assert
        nextCalled.Should().BeTrue();
        result.Should().BeOfType<Ok>();
    }

    [Fact]
    public async Task InvokeAsync_WhenMutationAndAdminKeyConfigured_AllowsValidAdminKey()
    {
        // Arrange
        _config["OBSERVATORY_API_KEY"].Returns("admin-key-12345");

        var httpContext = CreateHttpContext("POST", "admin-key-12345");
        var context = EndpointFilterInvocationContext.Create(httpContext);

        var nextCalled = false;
        ValueTask<object?> Next(EndpointFilterInvocationContext _)
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        }

        // Act
        var result = await _sut.InvokeAsync(context, Next);

        // Assert
        nextCalled.Should().BeTrue();
        result.Should().BeOfType<Ok>();
    }

    [Fact]
    public async Task InvokeAsync_WhenMutationAndAdminKeyConfigured_RejectsInvalidAdminKey()
    {
        // Arrange
        _config["OBSERVATORY_API_KEY"].Returns("admin-key-12345");

        var httpContext = CreateHttpContext("POST", "wrong-admin-key");
        var context = EndpointFilterInvocationContext.Create(httpContext);

        var nextCalled = false;
        ValueTask<object?> Next(EndpointFilterInvocationContext _)
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        }

        // Act
        var result = await _sut.InvokeAsync(context, Next);

        // Assert
        nextCalled.Should().BeFalse();
        result.Should().BeOfType<UnauthorizedHttpResult>();
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task InvokeAsync_WhenMutation_RejectsValidReadOnlyKey(string method)
    {
        _config["OBSERVATORY_API_KEY"].Returns("admin-key-12345");
        _config["OBSERVATORY_READONLY_API_KEY"].Returns("readonly-key-12345");
        var context = EndpointFilterInvocationContext.Create(CreateHttpContext(method, "readonly-key-12345"));
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
    public async Task InvokeAsync_WhenMutationAndAdminKeyConfigured_RejectsWrongLengthAdminKey()
    {
        // Arrange
        _config["OBSERVATORY_API_KEY"].Returns("admin-key-12345");

        var httpContext = CreateHttpContext("POST", "short"); // wrong length
        var context = EndpointFilterInvocationContext.Create(httpContext);

        var nextCalled = false;
        ValueTask<object?> Next(EndpointFilterInvocationContext _)
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        }

        // Act
        var result = await _sut.InvokeAsync(context, Next);

        // Assert
        nextCalled.Should().BeFalse();
        result.Should().BeOfType<UnauthorizedHttpResult>();
    }

    private static DefaultHttpContext CreateHttpContext(string method, string? key = null, ClaimsPrincipal? user = null)
    {
        var context = new DefaultHttpContext { Request = { Method = method }, User = user ?? new ClaimsPrincipal() };
        if (key is not null)
        {
            context.Request.Headers["X-Observatory-Key"] = key;
        }

        return context;
    }
}
