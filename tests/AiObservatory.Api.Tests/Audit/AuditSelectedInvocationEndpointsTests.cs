using System.Net;
using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using AiObservatory.Api.Audit;
using AiObservatory.Data.Audit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using NodaTime;
using NodaTime.Testing;

namespace AiObservatory.Api.Tests.Audit;

public sealed partial class AuditSelectedInvocationEndpointsTests
{
    [Fact]
    public async Task WireTimestampsAreExplicitUtcWithoutAnInstantJsonConverter()
    {
        var reader = new CountingReader { Outcome = "timestamps" };
        await using var app = await Host(reader);
        using var client = app.GetTestClient();
        using var response = await Send(client, Admin);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        json.RootElement.GetProperty("evidenceExpiresAt").GetDateTimeOffset().Should().Be(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero));
        json.RootElement.GetProperty("summaryExpiresAt").GetDateTimeOffset().Offset.Should().Be(TimeSpan.Zero);
        json.RootElement.GetProperty("requests")[0].GetProperty("deadline").GetDateTimeOffset().Offset.Should().Be(TimeSpan.Zero);
    }
    [Theory]
    [InlineData("missing", 404, "selected_binding_not_found")]
    [InlineData("conflict", 409, "selected_binding_conflict")]
    [InlineData("exception", 503, "selected_metadata_unavailable")]
    [InlineData("oversized", 503, "selected_metadata_unavailable")]
    [InlineData("wrong-run", 503, "selected_metadata_unavailable")]
    public async Task ReadFailuresAndOversizedSerializationUseOnlyFixedPrivateErrors(string outcome, int status, string code)
    {
        var reader = new CountingReader { Outcome = outcome };
        await using var app = await Host(reader);
        using var client = app.GetTestClient();
        using var response = await Send(client, Admin);
        ((int)response.StatusCode).Should().Be(status);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be("{\"code\":\"" + code + "\"}");
    }

    [Fact]
    public async Task ProtectedValuesNeverEnterLogsAndRejectedQueryDiagnosticBoundaryIsExplicit()
    {
        var capture = new LogCapture();
        var reader = new CountingReader { Outcome = "response" };
        const string credential = "PRIVATE_ADMIN_KEY_SENTINEL";
        await using var app = await Host(reader, new() { Logs = capture, AdminKey = credential });
        using var client = app.GetTestClient();
        using var success = await Send(client, credential);
        success.StatusCode.Should().Be(HttpStatusCode.OK);
        (await success.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("PRIVATE_RESPONSE_SENTINEL");
        reader.Outcome = "exception";
        using var failed = await Send(client, credential, "person");
        failed.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        using var bodyRequest = new HttpRequestMessage(HttpMethod.Get, AuditSelectedInvocationEndpoints.BindingRoute)
        {
            Content = new StringContent("PRIVATE_BODY_SENTINEL"),
        };
        bodyRequest.Headers.Add("X-Observatory-Key", credential);
        using var bodyResponse = await client.SendAsync(bodyRequest, TestContext.Current.CancellationToken);
        bodyResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var queryRequest = new HttpRequestMessage(HttpMethod.Get, AuditSelectedInvocationEndpoints.BindingRoute + "?PRIVATE_QUERY_SENTINEL=value");
        queryRequest.Headers.Add("X-Observatory-Key", credential);
        using var queryResponse = await client.SendAsync(queryRequest, TestContext.Current.CancellationToken);
        queryResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await queryResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().NotContain("PRIVATE_QUERY_SENTINEL");
        var logs = string.Join(Environment.NewLine, capture.Entries.Select(e => e.Message + e.Exception));
        logs.Should().NotContain(credential).And.NotContain("PRIVATE_BODY_SENTINEL").And.NotContain("PRIVATE_EXCEPTION_SENTINEL")
            .And.NotContain("PRIVATE_RESPONSE_SENTINEL");
        capture.Entries.Should().Contain(e => e.Category == "AuditSelectedInvocation" && e.Message == "Selected invocation metadata unavailable");
        var query = capture.Entries.Where(e => e.Message.Contains("PRIVATE_QUERY_SENTINEL", StringComparison.Ordinal)).ToArray();
        query.Should().NotBeEmpty("this synthetic host deliberately enables Information hosting diagnostics");
        query.Should().OnlyContain(e => e.Category == "Microsoft.AspNetCore.Hosting.Diagnostics" && e.Level == LogLevel.Information);
        query.Should().Contain(e => e.EventId.Id == 1 && e.Template ==
            "Request starting {Protocol} {Method} {Scheme}://{Host}{PathBase}{Path}{QueryString} - {ContentType} {ContentLength}");
        foreach (var entry in query)
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"{entry.Category} {entry.EventId} {entry.Level} {entry.Template}");
        }
    }

    private sealed record LogEntry(string Category, EventId EventId, LogLevel Level, string Message, string? Exception, string? Template);
    private sealed class LogCapture : ILoggerProvider
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, Entries);
        public void Dispose() { }
        private sealed class CaptureLogger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(new(category, eventId, logLevel, formatter(state, exception), exception?.ToString(),
                    (state as IEnumerable<KeyValuePair<string, object?>>)?.FirstOrDefault(p => p.Key == "{OriginalFormat}").Value?.ToString()));
        }
    }
    [Theory]
    [InlineData(false, true, false, false, 404)]
    [InlineData(true, false, false, false, 404)]
    [InlineData(true, true, false, false, 200)]
    [InlineData(true, true, true, false, 200)]
    [InlineData(true, true, false, true, 200)]
    public async Task RetainedBindingGetIsIndependentOfActionAndWorker(bool audit, bool bound, bool action, bool worker, int status)
    {
        var reader = new CountingReader();
        await using var app = await Host(reader, new() { AuditEnabled = audit, Bound = bound, ActionEnabled = action, WorkerEnabled = worker });
        using var client = app.GetTestClient();
        using var response = await Send(client, Admin);
        ((int)response.StatusCode).Should().Be(status);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        reader.Resolutions.Should().Be(status == 200 ? 1 : 0);
    }

    [Theory]
    [InlineData("Development", null, null, Admin, Viewer, 401)]
    [InlineData("Production", null, null, Admin, Viewer, 401)]
    [InlineData("Development", Viewer, null, Admin, Viewer, 403)]
    [InlineData("Production", Viewer, "person", Admin, Viewer, 403)]
    [InlineData("Development", Admin, null, Admin, Admin, 403)]
    [InlineData("Development", null, null, null, Viewer, 503)]
    [InlineData("Production", null, "person", Admin, Viewer, 200)]
    [InlineData("Development", "wrong", null, Admin, Viewer, 401)]
    [InlineData("Development", Admin + "," + Viewer, "person", Admin, Viewer, 401)]
    public async Task StrictAuthorityMatrix(string environment, string? key, string? subject, string? admin, string? viewer, int status)
    {
        var reader = new CountingReader();
        await using var app = await Host(reader, new() { Environment = environment, AdminKey = admin, ViewerKey = viewer });
        using var client = app.GetTestClient();
        using var response = await Send(client, key, subject);
        ((int)response.StatusCode).Should().Be(status);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        reader.Resolutions.Should().Be(status == 200 ? 1 : 0);
        reader.Calls.Should().Be(status == 200 ? 1 : 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultipleCredentialHeaderValuesFailBeforeReaderResolution(bool authenticated)
    {
        var reader = new CountingReader();
        await using var app = await Host(reader);
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, AuditSelectedInvocationEndpoints.BindingRoute);
        request.Headers.Add("X-Observatory-Key", [Admin, "PRIVATE_SECOND_KEY_SENTINEL"]);
        if (authenticated)
        {
            request.Headers.Add("X-Test-Authenticated-Subject", "person");
        }
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        reader.Resolutions.Should().Be(0);
    }

    [Theory]
    [InlineData("query")]
    [InlineData("body")]
    [InlineData("headerless-body")]
    [InlineData("transfer")]
    public async Task InvalidGetInputNeverResolvesReader(string input)
    {
        var reader = new CountingReader();
        await using var app = await Host(reader, new() { Input = input });
        using var client = app.GetTestClient();
        using var response = await Send(client, Admin);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        reader.Resolutions.Should().Be(0);
    }

    [Theory]
    [InlineData("POST", "/binding", 405)]
    [InlineData("PUT", "/binding", 405)]
    [InlineData("GET", "/unknown", 404)]
    [InlineData("POST", "/invoke", 401)]
    public async Task UnmappedOrWrongMethodResponsesRemainPrivate(string method, string suffix, int status)
    {
        var reader = new CountingReader();
        await using var app = await Host(reader);
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), AuditSelectedInvocationEndpoints.Prefix + suffix);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        ((int)response.StatusCode).Should().Be(status);
        response.Headers.CacheControl.Should().NotBeNull("unmapped and method-rejected responses still belong to the private prefix");
        response.Headers.CacheControl.NoStore.Should().BeTrue();
        reader.Resolutions.Should().Be(0);
    }

    [Fact]
    public async Task StartupAuthoritySurvivesMutableOptionsChangesAndReportsCurrentRevision()
    {
        var reader = new CountingReader();
        await using var app = await Host(reader);
        var options = app.Services.GetRequiredService<IOptions<AuditSelectedInvocationOptions>>().Value;
        options.RunId = Guid.NewGuid().ToString("D");
        options.ExpectedRevision = "999";
        options.FullScopeDigest = new string('b', 64);
        options.InvocationId = Guid.NewGuid().ToString("D");
        using var client = app.GetTestClient();
        using var response = await Send(client, Admin);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        json.RootElement.GetProperty("runId").GetGuid().Should().Be(RunId);
        json.RootElement.GetProperty("invocationId").GetGuid().Should().Be(InvocationId);
        json.RootElement.GetProperty("expectedRevision").GetInt64().Should().Be(7);
        json.RootElement.GetProperty("currentRevision").GetInt64().Should().Be(9);
        json.RootElement.GetProperty("fullScopeDigest").GetString().Should().Be(Digest);
    }

    [Fact]
    public async Task SharedTrustedCooldownAndGlobalIpLimitBothApplyBeforeReaderResolution()
    {
        var reader = new CountingReader();
        await using var app = await Host(reader, new() { IpLimit = 2 });
        using var client = app.GetTestClient();
        using var first = await Send(client, null, "first");
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        using var same = await Send(client, null, "first");
        same.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        using var other = await Send(client, null, "second");
        other.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        other.Headers.CacheControl!.NoStore.Should().BeTrue();
        reader.Resolutions.Should().Be(1);
    }

    [Fact]
    public async Task DistinctTrustedAuthoritiesHaveIndependentBuckets()
    {
        var reader = new CountingReader();
        await using var app = await Host(reader);
        using var client = app.GetTestClient();
        using var first = await Send(client, null, "first");
        using var second = await Send(client, null, "second");
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        reader.Resolutions.Should().Be(2);
    }
    private const string Admin = "selected-admin";
    private const string Viewer = "selected-viewer";
    private static readonly Guid RunId = Guid.Parse("4a0a3de8-572d-40b6-8d42-4b074b3ce080");
    private static readonly Guid InvocationId = Guid.Parse("bf326bf0-0762-4372-b9f6-5b8de7e223d9");
    private static readonly string Digest = new('a', 64);

    [Theory]
    [InlineData(Admin, 200, 1)]
    [InlineData(null, 401, 0)]
    public async Task StrictGetReadsOnlyAfterAuthority(string? key, int expected, int reads)
    {
        var reader = new CountingReader();
        await using var app = await Host(reader);
        using var client = app.GetTestClient();
        using var response = await Send(client, key);
        ((int)response.StatusCode).Should().Be(expected);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        reader.Calls.Should().Be(reads);
        reader.Resolutions.Should().Be(reads);
    }

    private sealed record HostSettings
    {
        public bool AuditEnabled { get; init; } = true;
        public bool Bound { get; init; } = true;
        public bool ActionEnabled { get; init; }
        public bool WorkerEnabled { get; init; }
        public string Environment { get; init; } = "Development";
        public string? AdminKey { get; init; } = Admin;
        public string? ViewerKey { get; init; } = Viewer;
        public int IpLimit { get; init; } = 1000;
        public string? Input { get; init; }
        public LogCapture? Logs { get; init; }
        public string ExpiresAtUtc { get; init; } = "2020-01-02T03:04:00Z";
        public CountingInvocation? Invocation { get; init; }
        public TimeProvider? Time { get; init; }
        public CancellationToken? RequestAbort { get; init; }
        public Stream? Body { get; init; }
        public TaskCompletionSource? HandlerFinished { get; init; }
        public TaskCompletionSource<bool>? HandlerOwnership { get; init; }
        public TaskCompletionSource<string>? OwnershipSignal { get; init; }
    }

    private static async Task<WebApplication> Host(CountingReader reader, HostSettings? settings = null)
    {
        settings ??= new();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = settings.Environment });
        builder.WebHost.UseTestServer();
        if (settings.Logs != null)
        {
            builder.Logging.AddProvider(settings.Logs);
            builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Information);
        }
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OBSERVATORY_API_KEY"] = settings.AdminKey,
            ["OBSERVATORY_READONLY_API_KEY"] = settings.ViewerKey,
            ["RateLimiting:ApiPermitLimit"] = settings.IpLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        builder.Services.Configure<AuditOptions>(o => { o.Enabled = settings.AuditEnabled; o.WorkerEnabled = settings.WorkerEnabled; });
        builder.Services.Configure<AuditSelectedInvocationOptions>(o =>
        {
            if (!settings.Bound)
            {
                return;
            }
            o.Enabled = settings.ActionEnabled;
            o.RunId = RunId.ToString("D"); o.InvocationId = InvocationId.ToString("D");
            o.FullScopeDigest = Digest; o.ExpectedRevision = "7"; o.ExpiresAtUtc = settings.ExpiresAtUtc;
        });
        builder.Services.AddScoped<IAuditSelectedInvocationMetadataReader>(_ => { reader.Resolutions++; return reader; });
        builder.Services.AddSingleton<IClock>(new FakeClock(Instant.FromUtc(2026, 10, 23, 12, 0)));
        builder.Services.AddSingleton<AuditSelectedInvocationLimiter>();
        builder.Services.AddSingleton(settings.Time ?? TimeProvider.System);
        if (settings.Invocation != null)
        {
            builder.Services.AddScoped<IAuditSelectedInvocationService>(_ => { settings.Invocation.Resolutions++; return settings.Invocation; });
        }
        builder.Services.AddRateLimiter(o => Program.ConfigureApiRateLimiter(o, builder.Configuration));
        var app = builder.Build();
        app.UseAuditSelectedInvocationPrivacy();
        app.UseRateLimiter();
        app.Use(async (context, next) =>
        {
            if (settings.RequestAbort is { } abort)
            {
                context.RequestAborted = abort;
            }
            if (settings.Body != null)
            {
                context.Request.Body = settings.Body;
            }
            switch (settings.Input)
            {
                case "query": context.Request.QueryString = new("?PRIVATE_QUERY_SENTINEL=value"); break;
                case "body": context.Request.ContentLength = 1; break;
                case "transfer": context.Request.Headers.TransferEncoding = "chunked"; break;
                case "headerless-body": context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyFeature()); break;
                default: break;
            }
            if (context.Request.Headers.TryGetValue("X-Test-Authenticated-Subject", out var subject))
            {
                context.User = new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, subject.ToString())], "test"));
            }
            try
            {
                await next(context);
            }
            finally
            {
                settings.OwnershipSignal?.TrySetResult("handler_finished");
                settings.HandlerOwnership?.TrySetResult(settings.Invocation?.Release.Task.IsCompleted != false);
                settings.HandlerFinished?.TrySetResult();
            }
        });
        app.MapAuditSelectedInvocationEndpoints();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private sealed class BodyFeature : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, string? key, string? subject = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, AuditSelectedInvocationEndpoints.BindingRoute);
        if (key != null)
        {
            request.Headers.Add("X-Observatory-Key", key);
        }
        if (subject != null)
        {
            request.Headers.Add("X-Test-Authenticated-Subject", subject);
        }
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private sealed class CountingReader : IAuditSelectedInvocationMetadataReader
    {
        public int Resolutions { get; set; }
        public int Calls { get; private set; }
        public string? Outcome { get; set; }
        public Task<SelectedRunMetadata?> ReadAsync(SelectedRunBinding binding, CancellationToken ct)
        {
            Calls++;
            binding.Should().Be(new SelectedRunBinding(RunId, Digest, 7));
            if (Outcome == "exception")
            {
                throw new InvalidOperationException("PRIVATE_EXCEPTION_SENTINEL");
            }
            if (Outcome == "conflict")
            {
                throw new AuditStoreException("selected_binding_conflict", 409);
            }
            if (Outcome == "missing")
            {
                return Task.FromResult<SelectedRunMetadata?>(null);
            }
            if (Outcome == "timestamps")
            {
                var now = Instant.FromUtc(2026, 10, 23, 12, 0);
                var requestId = Guid.NewGuid();
                return Task.FromResult<SelectedRunMetadata?>(new(RunId, Digest, 9, "collecting", "collecting", 0,
                    [requestId], [new(requestId, Guid.NewGuid(), new string('b', 64), now, "pending")], [], now, now, null, false));
            }
            return Task.FromResult<SelectedRunMetadata?>(new(Outcome == "wrong-run" ? Guid.NewGuid() : RunId, Digest, 9, "completed", "completed", 1,
                [], [], [], null, null, Outcome switch
                {
                    "oversized" => new string('x', 32 * 1024),
                    "response" => "PRIVATE_RESPONSE_SENTINEL",
                    _ => null,
                }, false));
        }
    }
}
