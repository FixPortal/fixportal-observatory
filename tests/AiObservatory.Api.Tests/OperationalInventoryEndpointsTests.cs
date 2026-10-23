using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiObservatory.Api.Endpoints;
using AiObservatory.Data.Audit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;

namespace AiObservatory.Api.Tests;

public sealed class OperationalInventoryEndpointsTests
{
    private const string Admin = "inventory-admin";
    private const string Viewer = "inventory-viewer";

    [Theory]
    [InlineData(false, Admin, false, Admin, Viewer, 404)]
    [InlineData(true, null, false, Admin, Viewer, 401)]
    [InlineData(true, Viewer, false, Admin, Viewer, 403)]
    [InlineData(true, Viewer, true, Admin, Viewer, 403)]
    [InlineData(true, null, false, null, Viewer, 503)]
    [InlineData(true, Admin, false, Admin, Admin, 403)]
    [InlineData(true, "wrong", false, Admin, Viewer, 401)]
    [InlineData(true, Admin + "," + Viewer, false, Admin, Viewer, 401)]
    public async Task DeniedRequestsNeverReadInventory(
        bool enabled,
        string? supplied,
        bool authenticated,
        string? admin,
        string? viewer,
        int expected
    )
    {
        var reader = new CountingReader();
        await using var app = await Host(reader, enabled, admin, viewer);
        using var client = app.GetTestClient();
        using var response = await Send(client, supplied, authenticated ? "person" : null);
        ((int)response.StatusCode).Should().Be(expected);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        reader.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultipleHeaderValuesNeverAcquireAuthority(bool authenticated)
    {
        var reader = new CountingReader();
        await using var app = await Host(reader);
        using var client = app.GetTestClient();
        using var request = Request(null, authenticated ? "person" : null);
        request.Headers.Add("X-Observatory-Key", [Admin, "other"]);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        reader.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData(Admin, null)]
    [InlineData(null, "person")]
    public async Task StrictAuthorityCanReadWithoutAuditFlag(string? key, string? principal)
    {
        var reader = new CountingReader();
        await using var app = await Host(reader);
        using var client = app.GetTestClient();
        using var response = await Send(client, key, principal);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        using var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
        body.RootElement.GetProperty("schemaVersion").GetInt32().Should().Be(1);
        body.RootElement.EnumerateObject()
            .Select(p => p.Name)
            .Should()
            .BeEquivalentTo(
                "schemaVersion",
                "observedAtUtc",
                "scope",
                "policy",
                "runs",
                "exportLeases",
                "captureRequests",
                "captures",
                "credentials",
                "machineCount"
            );
        reader.Calls.Should().Be(1);
    }

    [Fact]
    public async Task TrailingSlashRetainsPrivacyOnDeniedRoute()
    {
        var reader = new CountingReader();
        await using var app = await Host(reader);
        using var client = app.GetTestClient();
        using var response = await Send(client, null, suffix: "/");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.CacheControl.Should().NotBeNull("every inventory response must forbid caching");
        response.Headers.CacheControl.ToString().Should().Be("no-store");
        reader.Calls.Should().Be(0);
    }

    [Fact]
    public async Task BodyDetectionFeatureRejectsBodyWithoutLengthOrTransferEncoding()
    {
        var reader = new CountingReader();
        await using var app = await Host(
            reader,
            configureRequest: context =>
            {
                context.Request.ContentLength.Should().BeNull();
                context.Request.Headers.ContainsKey("Transfer-Encoding").Should().BeFalse();
                context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetectionFeature());
            }
        );
        using var client = app.GetTestClient();
        using var response = await Send(client, Admin);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl.ToString().Should().Be("no-store");
        reader.Calls.Should().Be(0);
    }

    [Fact]
    public async Task MappedRouteChecksCurrentFlagBeforeAuthorityOrReader()
    {
        var reader = new CountingReader();
        await using var app = await Host(reader);
        app.Services.GetRequiredService<IOptionsMonitor<OperationalInventoryOptions>>().CurrentValue.Enabled = false;
        using var client = app.GetTestClient();
        using var response = await Send(client, Admin);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        reader.Calls.Should().Be(0);
    }

    [Fact]
    public async Task BodyCannotSupplyAnAlternativeRead()
    {
        var reader = new CountingReader();
        await using var app = await Host(reader);
        using var client = app.GetTestClient();
        using var request = Request(Admin, null);
        request.Content = new StringContent("{\"instance\":\"other\"}");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        reader.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("?instance=other")]
    [InlineData("?sql=SELECT")]
    public async Task QueryCannotChangeTheFixedRead(string query)
    {
        var reader = new CountingReader();
        await using var app = await Host(reader);
        using var client = app.GetTestClient();
        using var response = await Send(client, Admin, suffix: query);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        reader.Calls.Should().Be(0);
    }

    [Fact]
    public async Task DedicatedLimitRunsAfterAuthorityAndSharesAdminClass()
    {
        var reader = new CountingReader();
        await using var app = await Host(reader);
        using var client = app.GetTestClient();
        using var denied = await Send(client, "untrusted");
        using var accepted = await Send(client, Admin);
        using var limited = await Send(client, Admin);
        denied.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        accepted.StatusCode.Should().Be(HttpStatusCode.OK);
        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        limited.Headers.CacheControl!.NoStore.Should().BeTrue();
        reader.Calls.Should().Be(1);
    }

    [Fact]
    public async Task AuthenticatedIdentityIsPartitionedAfterMiddlewareAuthentication()
    {
        var reader = new CountingReader();
        await using var app = await Host(reader);
        using var client = app.GetTestClient();
        using var first = await Send(client, null, "first");
        using var repeat = await Send(client, "rotated-unused-header", "first");
        using var second = await Send(client, null, "second");
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        repeat.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        reader.Calls.Should().Be(2);
    }

    [Fact]
    public async Task IpLimitAlsoConstrainsDifferentAuthenticatedPrincipals()
    {
        var reader = new CountingReader();
        await using var app = await Host(reader, ipLimit: 1);
        using var client = app.GetTestClient();
        using var first = await Send(client, null, "first");
        using var second = await Send(client, null, "second");
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        second.Headers.CacheControl!.NoStore.Should().BeTrue();
        reader.Calls.Should().Be(1);
    }

    [Fact]
    public void DedicatedLimiterBoundsCardinalityAndReleasesExpiredPartitions()
    {
        var clock = new FakeClock(Instant.FromUtc(2026, 10, 23, 12, 0));
        var limiter = new OperationalInventoryLimiter(clock);
        for (var i = 0; i < OperationalInventoryLimiter.MaximumPartitions; i++)
        {
            limiter.TryAcquire($"verified-{i}").Should().BeTrue();
        }
        limiter.TryAcquire("overflow").Should().BeFalse();
        clock.Advance(Duration.FromSeconds(59));
        limiter.TryAcquire("verified-0").Should().BeFalse();
        clock.Advance(Duration.FromSeconds(1));
        limiter.TryAcquire("overflow").Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureAndOversizedSerializationReturnOnlyFixedError(bool oversized)
    {
        var reader = new CountingReader { Fail = !oversized };
        await using var app = await Host(reader, oversized: oversized);
        using var client = app.GetTestClient();
        using var response = await Send(client, Admin);
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should()
            .Be("{\"code\":\"inventory_unavailable\"}");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    private static async Task<WebApplication> Host(
        CountingReader reader,
        bool enabled = true,
        string? admin = Admin,
        string? viewer = Viewer,
        int ipLimit = 1000,
        bool oversized = false,
        Action<HttpContext>? configureRequest = null
    )
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["OBSERVATORY_API_KEY"] = admin,
                ["OBSERVATORY_READONLY_API_KEY"] = viewer,
                ["RateLimiting:ApiPermitLimit"] = ipLimit.ToString(),
            }
        );
        builder.Services.Configure<OperationalInventoryOptions>(o => o.Enabled = enabled);
        builder.Services.AddSingleton<IAuditOperationalInventoryReader>(reader);
        builder.Services.AddSingleton<IClock>(new FakeClock(Instant.FromUtc(2026, 10, 23, 12, 0)));
        builder.Services.AddSingleton<OperationalInventoryLimiter>();
        builder.Services.AddRateLimiter(o => Program.ConfigureApiRateLimiter(o, builder.Configuration));
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.Converters.Add(new InstantConverter());
            if (oversized)
            {
                o.SerializerOptions.Converters.Add(new OversizedInventoryConverter());
            }
        });
        var app = builder.Build();
        app.UseOperationalInventoryPrivacy();
        app.UseRateLimiter();
        app.Use(
            async (context, next) =>
            {
                configureRequest?.Invoke(context);
                if (context.Request.Headers.TryGetValue("X-Test-Authenticated-Subject", out var subject))
                {
                    context.User = new(
                        new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, subject.ToString())], "test")
                    );
                }
                await next(context);
            }
        );
        app.MapOperationalInventoryEndpoint();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static HttpRequestMessage Request(string? key, string? principal, string suffix = "")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, OperationalInventoryEndpoints.Route + suffix);
        if (key != null)
        {
            request.Headers.Add("X-Observatory-Key", key);
        }
        if (principal != null)
        {
            request.Headers.Add("X-Test-Authenticated-Subject", principal);
        }
        return request;
    }

    private static async Task<HttpResponseMessage> Send(
        HttpClient client,
        string? key,
        string? principal = null,
        string suffix = ""
    )
    {
        using var request = Request(key, principal, suffix);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private sealed class BodyDetectionFeature : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private sealed class CountingReader : IAuditOperationalInventoryReader
    {
        public int Calls { get; private set; }
        public bool Fail { get; init; }

        public Task<AuditOperationalInventory> ReadAsync(CancellationToken cancellationToken)
        {
            Calls++;
            if (Fail)
            {
                throw new InvalidOperationException("synthetic-private-connection-detail");
            }
            return Task.FromResult(
                new AuditOperationalInventory(
                    1,
                    Instant.FromUtc(2026, 10, 23, 12, 0),
                    new(true, true),
                    new(false, null, null, null),
                    new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
                    new(0, 0, 0, 0, 0, 0, 0),
                    new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
                    new(0, 0, 0, 0),
                    new(0, 0, 0, 0, null, null),
                    0
                )
            );
        }
    }

    private sealed class InstantConverter : JsonConverter<Instant>
    {
        public override Instant Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, Instant value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToDateTimeUtc());
    }

    private sealed class OversizedInventoryConverter : JsonConverter<AuditOperationalInventory>
    {
        public override AuditOperationalInventory Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options
        ) => throw new NotSupportedException();

        public override void Write(
            Utf8JsonWriter writer,
            AuditOperationalInventory value,
            JsonSerializerOptions options
        ) => writer.WriteStringValue(new string('x', OperationalInventoryEndpoints.MaximumResponseBytes));
    }
}
