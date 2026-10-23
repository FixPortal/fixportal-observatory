using System.Net;
using System.Text;
using System.Text.Json;
using AiObservatory.Api.Audit;
using AwesomeAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AiObservatory.Api.Tests.Audit;

public sealed partial class AuditSelectedInvocationEndpointsTests
{
    [Theory]
    [InlineData("budget")]
    [InlineData("caller")]
    [InlineData("host")]
    public async Task PostCancellationStillAwaitsHeldOwnedInvocation(string cause)
    {
        var invocation = new CountingInvocation { Hold = true };
        var time = new PostTime();
        using var abort = new CancellationTokenSource();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ownership = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var signal = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        invocation.OwnershipSignal = signal;
        await using var app = await Host(
            new(),
            Active(invocation) with
            {
                Time = time,
                RequestAbort = abort.Token,
                HandlerFinished = finished,
                HandlerOwnership = ownership,
                OwnershipSignal = signal,
            }
        );
        using var client = app.GetTestClient();
        var response = Post(client);
        try
        {
            await invocation.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            time.Created.Should().Be(1);
            time.InitialDue.Should().Be(TimeSpan.FromMinutes(4));
            if (cause == "budget")
            {
                time.Advance(TimeSpan.FromMinutes(4));
            }
            else if (cause == "caller")
            {
                await abort.CancelAsync();
            }
            else
            {
                app.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
            }
            (await signal.Task.WaitAsync(TestContext.Current.CancellationToken))
                .Should()
                .Be(
                    "cancellation",
                    "premature server completion is itself a positive ownership violation, even if it disposed the cancellation budget"
                );
            await invocation.Cancelled.Task.WaitAsync(TestContext.Current.CancellationToken);
            finished.Task.IsCompleted.Should().BeFalse("the request must not detach the owned invocation");
        }
        finally
        {
            invocation.Release.TrySetResult();
            try
            {
                await response;
            }
            catch (OperationCanceledException) when (cause == "caller")
            {
                // Settle the request even when an assertion/control fails before response delivery.
            }
        }
        try
        {
            using var completed = await response;
            completed.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        }
        catch (OperationCanceledException) when (cause == "caller")
        {
            // A disconnected caller need not receive a response; owned work was still awaited.
        }
        await finished.Task.WaitAsync(TestContext.Current.CancellationToken);
        (await ownership.Task).Should().BeTrue("server completion must occur after the owned invocation release");
    }

    [Fact]
    public async Task PostSingleFourMinuteBudgetIncludesBodyReadAndIsNotReset()
    {
        var invocation = new CountingInvocation { Hold = true };
        var time = new PostTime();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var body = new HeldPostBody(
            Encoding.UTF8.GetBytes(
                "{\"schemaVersion\":1,\"expectedRevision\":7,\"fullScopeDigest\":\"" + Digest + "\"}"
            )
        );
        await using var app = await Host(
            new(),
            Active(invocation) with
            {
                Time = time,
                Body = body,
                HandlerFinished = finished,
            }
        );
        using var client = app.GetTestClient();
        var response = Post(client);
        try
        {
            await body.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            time.Created.Should().Be(1, "budget must start before body parsing");
            time.Advance(TimeSpan.FromMinutes(3));
            body.Release.TrySetResult();
            await invocation.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            time.Created.Should().Be(1, "body parsing must not restart the four-minute timer");
            time.Advance(TimeSpan.FromMinutes(1));
            await invocation.Cancelled.Task.WaitAsync(TestContext.Current.CancellationToken);
            finished.Task.IsCompleted.Should().BeFalse("the server still owns the invocation until release");
        }
        finally
        {
            body.Release.TrySetResult();
            invocation.Release.TrySetResult();
            await response;
        }
        using var completed = await response;
        completed.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    private sealed class HeldPostBody(byte[] bytes) : MemoryStream(bytes)
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class PostTime : TimeProvider
    {
        private readonly List<PostTimer> _timers = [];
        private TimeSpan _elapsed;
        public int Created => _timers.Count;
        public TimeSpan InitialDue => _timers[0].Due;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new PostTimer(callback, state, _elapsed + dueTime);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan amount)
        {
            _elapsed += amount;
            foreach (var timer in _timers.ToArray())
            {
                timer.Fire(_elapsed);
            }
        }

        private sealed class PostTimer(TimerCallback callback, object? state, TimeSpan due) : ITimer
        {
            private bool _disposed;
            public TimeSpan Due { get; } = due;

            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Fire(TimeSpan elapsed)
            {
                if (!_disposed && elapsed >= Due)
                {
                    _disposed = true;
                    callback(state);
                }
            }

            public void Dispose() => _disposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    [Theory]
    [InlineData("off", 503)]
    [InlineData("worker", 409)]
    [InlineData("expired", 409)]
    [InlineData("audit-off", 404)]
    [InlineData("unbound", 404)]
    public async Task PostOuterGatesNeverResolveExecution(string gate, int status)
    {
        var invocation = new CountingInvocation();
        var settings = gate switch
        {
            "off" => Active(invocation) with { ActionEnabled = false },
            "worker" => Active(invocation) with { WorkerEnabled = true },
            "expired" => Active(invocation) with { ExpiresAtUtc = "2020-01-02T03:04:00Z" },
            "audit-off" => Active(invocation) with { AuditEnabled = false },
            _ => Active(invocation) with { Bound = false, ActionEnabled = false },
        };
        await using var app = await Host(new(), settings);
        using var client = app.GetTestClient();
        using var response = await Post(client);
        ((int)response.StatusCode).Should().Be(status);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        invocation.Resolutions.Should().Be(0);
        invocation.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("malformed", 400, "selected_request_invalid", "rejected")]
    [InlineData("oversized", 413, "selected_body_limit", "rejected")]
    [InlineData("content-type", 415, "selected_content_type", "rejected")]
    [InlineData("stale", 409, "selected_binding_conflict", "conflict")]
    public async Task PostParserFailuresPreserveHttpCodeAndStatusWithoutExecution(
        string input,
        int httpStatus,
        string code,
        string status
    )
    {
        var invocation = new CountingInvocation();
        await using var app = await Host(new(), Active(invocation));
        using var client = app.GetTestClient();
        var body = input switch
        {
            "malformed" => "{}",
            "oversized" => new string(' ', 1025),
            "stale" => "{\"schemaVersion\":1,\"expectedRevision\":8,\"fullScopeDigest\":\"" + Digest + "\"}",
            _ => null,
        };
        using var response = await Post(
            client,
            body,
            contentType: input == "content-type" ? "text/plain" : "application/json"
        );
        ((int)response.StatusCode).Should().Be(httpStatus);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
        json.RootElement.GetProperty("status").GetString().Should().Be(status);
        json.RootElement.GetProperty("code").GetString().Should().Be(code);
        invocation.Resolutions.Should().Be(0);
        invocation.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ActualGetConsumesSubsequentPostSlotWithoutExecutionResolution()
    {
        var invocation = new CountingInvocation();
        await using var app = await Host(new(), Active(invocation));
        using var client = app.GetTestClient();
        using var get = await Send(client, Admin);
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        using var post = await Post(client);
        post.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        post.Headers.CacheControl!.NoStore.Should().BeTrue();
        invocation.Resolutions.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostInvocationFaultOrOversizeIsAmbiguousNeverNominalStoreConflict(bool oversized)
    {
        var invocation = new CountingInvocation { Fault = !oversized, Oversized = oversized };
        await using var app = await Host(new(), Active(invocation));
        using var client = app.GetTestClient();
        using var response = await Post(client);
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var bytes = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        bytes.Length.Should().BeLessThanOrEqualTo(8192);
        using var json = JsonDocument.Parse(bytes);
        json.RootElement.GetProperty("status").GetString().Should().Be("ambiguous");
        json.RootElement.GetProperty("current").ValueKind.Should().Be(JsonValueKind.Null);
        invocation.Calls.Should().Be(1);
    }

    [Fact]
    public async Task PostReturnsCompactPendingOnlyAfterExactBodyBinding()
    {
        var invocation = new CountingInvocation();
        await using var app = await Host(new(), Active(invocation));
        using var client = app.GetTestClient();
        using var response = await Post(client);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        invocation.Calls.Should().Be(1);
        invocation.Resolutions.Should().Be(1);
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
        json.RootElement.GetProperty("status").GetString().Should().Be("pending");
        json.RootElement.EnumerateObject()
            .Select(p => p.Name)
            .Should()
            .BeEquivalentTo(
                "schemaVersion",
                "status",
                "code",
                "runId",
                "invocationId",
                "fullScopeDigest",
                "expectedRevision",
                "current"
            );
    }

    private static HostSettings Active(CountingInvocation invocation) =>
        new()
        {
            ActionEnabled = true,
            ExpiresAtUtc = "2030-01-02T03:04:00Z",
            Invocation = invocation,
        };

    private static async Task<HttpResponseMessage> Post(
        HttpClient client,
        string? body = null,
        string? subject = null,
        string contentType = "application/json"
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, AuditSelectedInvocationEndpoints.Prefix + "/invoke")
        {
            Content = new StringContent(
                body ?? "{\"schemaVersion\":1,\"expectedRevision\":7,\"fullScopeDigest\":\"" + Digest + "\"}",
                Encoding.UTF8,
                contentType
            ),
        };
        request.Headers.Add("X-Observatory-Key", Admin);
        request.Headers.Add("Idempotency-Key", InvocationId.ToString("D"));
        if (subject != null)
        {
            request.Headers.Add("X-Test-Authenticated-Subject", subject);
        }
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private sealed class CountingInvocation : IAuditSelectedInvocationService
    {
        public TaskCompletionSource<string>? OwnershipSignal { get; set; }
        public int Resolutions { get; set; }
        public int Calls { get; private set; }
        public bool Fault { get; init; }
        public bool Oversized { get; init; }
        public bool Hold { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<SelectedInvocationOutcome> InvokeAsync(
            SelectedInvocationAuthority authority,
            SelectedInvocationRequest request,
            CancellationToken ct
        )
        {
            Calls++;
            authority.Binding.RunId.Should().Be(RunId);
            request.ExpectedRevision.Should().Be(7);
            if (Hold)
            {
                using var registration = ct.Register(() =>
                {
                    OwnershipSignal?.TrySetResult("cancellation");
                    Cancelled.TrySetResult();
                });
                Entered.TrySetResult();
                await Release.Task;
                ct.ThrowIfCancellationRequested();
            }
            if (Fault)
            {
                throw new AiObservatory.Data.Audit.AuditStoreException("selected_binding_conflict", 409);
            }
            if (Oversized)
            {
                return new(
                    "admitted",
                    "selected_admitted",
                    new(9, "executing", "verifying", 1, new string('x', 8192), false, null, null)
                );
            }
            return new("pending", "selected_pending");
        }
    }
}
