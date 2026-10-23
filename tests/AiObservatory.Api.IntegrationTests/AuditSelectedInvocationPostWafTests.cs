using System.Data.Common;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using AiObservatory.Api.Audit;
using AiObservatory.Audit;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using AiObservatory.Data;
using AiObservatory.Data.Audit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime.Testing;
using NodaTime;
using Npgsql;

namespace AiObservatory.Api.IntegrationTests;

public sealed partial class AuditSelectedInvocationEndpointsWafTests
{
    [Theory]
    [InlineData("none", 200, "completed", 1)]
    [InlineData("before", 503, "ambiguous", 0)]
    [InlineData("after", 503, "ambiguous", 0)]
    [InlineData("post", 503, "ambiguous", 1)]
    [InlineData("transient", 200, "admitted", 1)]
    [InlineData("terminal", 200, "admitted", 1)]
    public async Task PostCommitAndPostAdmissionExceptionsRemainAmbiguousWithReachableControl(string mode, int status, string outcome, int verifiers)
    {
        await using var factory = Factory(true);
        var id = Guid.NewGuid();
        var scope = Scope();
        ActiveBinding(factory, id, scope);
        await factory.InitializeAsync();
        await SeedSelected(factory, id, scope);
        await using var services = factory.Services.CreateAsyncScope();
        var db = services.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        var before = await Facts(db, TestContext.Current.CancellationToken);
        var witness = new PostCommitWitness(mode);
        var entry = new PostCoreEntry(witness);
        await using var host = Instrument(factory, db, witness, entry);
        using var client = host.CreateClient();
        witness.Arm();
        using var response = await SendPost(client, factory, scope, "first");
        RequireAdmissionWitness(mode, witness, entry, verifiers);
        ((int)response.StatusCode).Should().Be(status);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var bytes = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        bytes.Length.Should().BeLessThanOrEqualTo(8192);
        using var json = JsonDocument.Parse(bytes);
        json.RootElement.GetProperty("status").GetString().Should().Be(outcome);
        witness.Faults.Should().Be(mode is "none" or "transient" or "terminal" ? 0 : 1);
        var row = (await services.ServiceProvider.GetRequiredService<AuditStore>().GetRunAsync(id, TestContext.Current.CancellationToken))!;
        if (mode is "before" or "after")
        {
            entry.AdvisoryLocks.Should().Be(1, "only admission may enter; a later read-only metadata transaction is not core entry");
            witness.Transactions.Should().Be(1);
            row.Attempts.Should().Be(mode == "before" ? 0 : 1);
            json.RootElement.GetProperty("current").ValueKind.Should().Be(JsonValueKind.Null);
            if (mode == "before")
            {
                (await Facts(db, TestContext.Current.CancellationToken)).Should().Be(before);
            }
        }
        else
        {
            entry.AdvisoryLocks.Should().BeGreaterThan(1, "the positive control must reach the actual leased core");
            row.Attempts.Should().Be(1);
            row.State.Should().Be(mode switch { "none" => "completed", "terminal" => "failed", _ => "executing" });
            if (mode == "none")
            {
                json.RootElement.GetProperty("current").GetProperty("state").GetString().Should().Be("completed");
            }
        }
        if (mode != "before")
        {
            var settled = await Facts(db, TestContext.Current.CancellationToken);
            using var again = await SendPost(client, factory, scope, "old-authority");
            again.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            witness.VerifierCalls.Should().Be(verifiers);
            (await Facts(db, TestContext.Current.CancellationToken)).Should().Be(settled);
        }
    }

    private static void RequireAdmissionWitness(string mode, PostCommitWitness witness, PostCoreEntry entry, int verifiers)
    {
        witness.VerifierCalls.Should().Be(verifiers);
        if (mode is "before" or "after")
        {
            entry.AdvisoryLocks.Should().Be(1, "no mutating core entry is allowed after admission uncertainty");
            witness.Transactions.Should().Be(1);
        }
    }

    [Theory]
    [InlineData("budget")]
    [InlineData("caller")]
    [InlineData("host")]
    [InlineData("cancel-run")]
    [InlineData("lease-expiry")]
    public async Task PostHeldVerifierCancellationAndOwnerLossNeverPublishLate(string cause)
    {
        await using var factory = Factory(true);
        var id = Guid.NewGuid();
        var scope = Scope();
        ActiveBinding(factory, id, scope);
        await factory.InitializeAsync();
        await SeedSelected(factory, id, scope);
        await using var services = factory.Services.CreateAsyncScope();
        var db = services.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        var witness = new PostCommitWitness("hold");
        using var probe = new PostRequestProbe();
        var time = new WafBudgetTime();
        await using var host = Instrument(factory, db, witness, new(witness), probe, time);
        using var client = host.CreateClient();
        witness.Arm();
        var response = SendPost(client, factory, scope, "held");
        try
        {
            await witness.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            time.Created.Should().Be(1);
            time.Due.Should().Be(TimeSpan.FromMinutes(4));
            switch (cause)
            {
                case "budget": time.Fire(); break;
                case "caller": await probe.Abort.CancelAsync(); break;
                case "host": host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication(); break;
                case "cancel-run": await services.ServiceProvider.GetRequiredService<AuditStore>().CancelRunAsync(id, TestContext.Current.CancellationToken); break;
                case "lease-expiry": ((FakeClock)factory.ClockOverride!).Advance(Duration.FromMinutes(6)); break;
                default: throw new InvalidOperationException("unknown_test_case");
            }
            if (cause is "budget" or "caller" or "host")
            {
                await witness.Cancelled.Task.WaitAsync(TestContext.Current.CancellationToken);
            }
            probe.Finished.Task.IsCompleted.Should().BeFalse("the server still owns the held verifier and renewal finally");
        }
        finally
        {
            witness.Release.TrySetResult();
        }
        try
        {
            using var completed = await response;
            var body = await completed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            if (cause == "host" && completed.StatusCode == HttpStatusCode.InternalServerError)
            {
                // WAF can dispose its provider before ContentHttpResult resolves its logger.
                body.Should().Contain("ObjectDisposedException").And.Contain("IServiceProvider").And.Contain("ContentHttpResult.ExecuteAsync");
            }
            else
            {
                completed.StatusCode.Should().Be(cause is "cancel-run" or "lease-expiry" ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable);
                using var json = JsonDocument.Parse(body);
                json.RootElement.GetProperty("status").GetString().Should().Be(cause is "cancel-run" or "lease-expiry" ? "admitted" : "ambiguous");
            }
        }
        catch (OperationCanceledException) when (cause is "caller" or "host")
        {
            // A disconnected client need not receive bytes, but the server work is awaited.
        }
        await probe.Finished.Task.WaitAsync(TestContext.Current.CancellationToken);
        var run = (await services.ServiceProvider.GetRequiredService<AuditStore>().GetRunAsync(id, TestContext.Current.CancellationToken))!;
        run.Attempts.Should().Be(1);
        run.Result.Should().BeNull("neither canceled work nor a lost owner may publish after release");
        run.State.Should().Be(cause == "cancel-run" ? "cancelled" : "executing");
        witness.VerifierCalls.Should().Be(1);
    }

    [Fact]
    public async Task PostActualProceedAcknowledgementWithFreshRevisionIsRefusedWithoutWrites()
    {
        await using var factory = Factory(true);
        var id = Guid.NewGuid();
        var scope = Scope();
        ActiveBinding(factory, id, scope);
        factory.ConfigurationOverrides[AuditSelectedInvocationOptions.SectionName + ":ExpectedRevision"] = "1";
        await factory.InitializeAsync();
        await SeedSelected(factory, id, scope, collecting: true);
        await using var services = factory.Services.CreateAsyncScope();
        var store = services.ServiceProvider.GetRequiredService<AuditStore>();
        await store.ProceedRunAsync(id, TestContext.Current.CancellationToken);
        (await store.GetRunAsync(id, TestContext.Current.CancellationToken))!.Revision.Should().Be(1);
        var db = services.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        var before = await Facts(db, TestContext.Current.CancellationToken);
        using var client = factory.CreateAdminClient();
        using var response = await SendPost(client, factory, scope);
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await Facts(db, TestContext.Current.CancellationToken)).Should().Be(before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostPendingAndBusyAreNormalNoWriteResultsWithoutDefaultPolicy(bool busy)
    {
        await using var factory = Factory(true);
        var id = Guid.NewGuid();
        var scope = Scope();
        ActiveBinding(factory, id, scope);
        await factory.InitializeAsync();
        await using var services = factory.Services.CreateAsyncScope();
        var db = services.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        var store = services.ServiceProvider.GetRequiredService<AuditStore>();
        if (busy)
        {
            await store.CreateRunAsync(scope, [], "post-live-unrelated", false, Now, TestContext.Current.CancellationToken);
            (await store.TryLeaseRunAsync(Guid.NewGuid(), Now, TestContext.Current.CancellationToken)).Should().NotBeNull();
        }
        await SeedSelected(factory, id, scope, collecting: !busy);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"AuditRetentionSettings\"", TestContext.Current.CancellationToken);
        var before = await Facts(db, TestContext.Current.CancellationToken);
        using var client = factory.CreateAdminClient();
        using var response = await SendPost(client, factory, scope);
        response.StatusCode.Should().Be(busy ? HttpStatusCode.Conflict : HttpStatusCode.Accepted);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        json.RootElement.GetProperty("status").GetString().Should().Be(busy ? "busy" : "pending");
        (await Facts(db, TestContext.Current.CancellationToken)).Should().Be(before);
    }

    [Fact]
    public async Task PostConcurrentDistinctAuthoritiesAdmitOnceAndRestartCannotReuseOldAuthority()
    {
        await using var factory = Factory(true);
        var id = Guid.NewGuid();
        var scope = Scope();
        ActiveBinding(factory, id, scope);
        await factory.InitializeAsync();
        await SeedSelected(factory, id, scope);
        await using var services = factory.Services.CreateAsyncScope();
        var db = services.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        var witness = new PostCommitWitness("none");
        await using (var host = Instrument(factory, db, witness, new(witness)))
        {
            using var client = host.CreateClient();
            witness.Arm();
            var responses = await Task.WhenAll(SendPost(client, factory, scope, "one"), SendPost(client, factory, scope, "two"));
            try
            {
                responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
                responses.Count(r => r.StatusCode == HttpStatusCode.ServiceUnavailable).Should().Be(1);
                witness.VerifierCalls.Should().Be(1);
            }
            finally
            {
                foreach (var response in responses)
                {
                    response.Dispose();
                }
            }
        }
        // The original GUID database owner is still alive; this derived host owns no database cleanup.
        await using var restarted = Instrument(factory, db, witness, new(witness));
        using var retryClient = restarted.CreateClient();
        var before = await Facts(db, TestContext.Current.CancellationToken);
        using var retry = await SendPost(retryClient, factory, scope, "restart");
        retry.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        witness.VerifierCalls.Should().Be(1);
        (await Facts(db, TestContext.Current.CancellationToken)).Should().Be(before);
    }

    private static void ActiveBinding(AiObservatoryApiFactory factory, Guid id, AuditScope scope)
    {
        Bind(factory, id, scope);
        factory.ConfigurationOverrides[AuditSelectedInvocationOptions.SectionName + ":Enabled"] = "true";
        factory.ConfigurationOverrides[AuditSelectedInvocationOptions.SectionName + ":ExpiresAtUtc"] = "2030-01-02T03:04:00Z";
    }

    private static async Task SeedSelected(AiObservatoryApiFactory factory, Guid id, AuditScope scope, bool collecting = false)
    {
        await using var services = factory.Services.CreateAsyncScope();
        var db = services.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        var ct = TestContext.Current.CancellationToken;
        if (collecting)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"AuditMachines\" (\"Id\",\"CreatedAt\") VALUES ({scope.Sources[0].MachineId},{Now})", ct);
        }
        var created = await services.ServiceProvider.GetRequiredService<AuditStore>().CreateRunAsync(scope, [], "selected-post-waf", collecting, Now, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"AuditRuns\" SET \"Id\"={id} WHERE \"Id\"={created}", ct);
    }

    private static WebApplicationFactory<Program> Instrument(AiObservatoryApiFactory owner, AiObservatoryDbContext ownedDb, PostCommitWitness witness,
        PostCoreEntry entry, PostRequestProbe? probe = null, TimeProvider? time = null) =>
        owner.WithWebHostBuilder(builder => builder.ConfigureLogging(logging => logging.ClearProviders()).ConfigureTestServices(services =>
        {
            var fixture = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TEST_DB_CONNECTION"));
            var owned = new NpgsqlConnectionStringBuilder(ownedDb.Database.GetConnectionString());
            owned.Host.Should().Be(fixture.Host);
            owned.Port.Should().Be(fixture.Port);
            owned.Database.Should().StartWith("aiobs_test_api_");
            Guid.TryParseExact(owned.Database["aiobs_test_api_".Length..], "N", out _).Should().BeTrue();
            fixture.Database = owned.Database;
            var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
                .UseNpgsql(fixture.ConnectionString, o => o.UseNodaTime()).AddInterceptors(witness, entry).Options;
            services.RemoveAll<DbContextOptions<AiObservatoryDbContext>>();
            services.AddSingleton(options);
            services.RemoveAll<IAuditRunVerifier>();
            services.AddSingleton<IAuditRunVerifier>(witness);
            services.AddSingleton<IStartupFilter>(new PostIdentityStartup(probe));
            if (time != null)
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(time);
            }
        }));

    private static async Task<HttpResponseMessage> SendPost(HttpClient client, AiObservatoryApiFactory factory, AuditScope scope, string? subject = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, AuditSelectedInvocationEndpoints.InvocationRoute)
        {
            Content = new StringContent("{\"schemaVersion\":1,\"expectedRevision\":"
                + factory.ConfigurationOverrides[AuditSelectedInvocationOptions.SectionName + ":ExpectedRevision"] + ",\"fullScopeDigest\":\""
                + AuditCanonicalJson.ContentHash(AuditCanonicalJson.Serialize(scope)) + "\"}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", factory.ConfigurationOverrides[AuditSelectedInvocationOptions.SectionName + ":InvocationId"]);
        if (subject != null)
        {
            request.Headers.Add("X-Post-Test-Subject", subject);
        }
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private sealed class PostIdentityStartup(PostRequestProbe? probe) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, invokeNext) =>
            {
                if (probe != null)
                {
                    context.RequestAborted = probe.Abort.Token;
                }
                if (context.Request.Headers.TryGetValue("X-Post-Test-Subject", out var subject))
                {
                    context.User = new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, subject.ToString())], "synthetic-entra"));
                }
                try
                {
                    await invokeNext(context);
                }
                finally
                {
                    probe?.Finished.TrySetResult();
                }
            });
            next(app);
        };
    }

    private sealed class PostRequestProbe : IDisposable
    {
        public CancellationTokenSource Abort { get; } = new();
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Dispose() => Abort.Dispose();
    }

    private sealed class WafBudgetTime : TimeProvider
    {
        private readonly List<BudgetTimer> _timers = [];
        public int Created => _timers.Count;
        public TimeSpan Due { get; private set; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Due = dueTime;
            var timer = new BudgetTimer(callback, state);
            _timers.Add(timer);
            return timer;
        }
        public void Fire()
        {
            foreach (var timer in _timers.ToArray())
            {
                timer.Fire();
            }
        }
        private sealed class BudgetTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Fire() { if (!_disposed) { callback(state); } }
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class PostCoreEntry(PostCommitWitness witness) : DbCommandInterceptor
    {
        private int _locks;
        public int AdvisoryLocks => Volatile.Read(ref _locks);
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (witness.Armed && command.CommandText.Contains("pg_advisory_xact_lock", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _locks);
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class PostCommitWitness(string mode) : DbTransactionInterceptor, IAuditRunVerifier
    {
        private int _remaining;
        private int _faults;
        private int _verifiers;
        private int _transactions;
        public bool Armed { get; private set; }
        public int Faults => Volatile.Read(ref _faults);
        public int VerifierCalls => Volatile.Read(ref _verifiers);
        public int Transactions => Volatile.Read(ref _transactions);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Arm() { _remaining = mode == "none" ? 0 : 1; Armed = true; }
        private bool TakeFault() => Armed && Interlocked.Exchange(ref _remaining, 0) == 1;
        public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData,
            DbTransaction result, CancellationToken cancellationToken = default)
        {
            if (Armed)
            {
                Interlocked.Increment(ref _transactions);
            }
            return ValueTask.FromResult(result);
        }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if ((mode == "before" || mode == "post" && VerifierCalls > 0) && TakeFault())
            {
                Interlocked.Increment(ref _faults);
                throw new AuditStoreException("selected_binding_conflict", 409);
            }
            return ValueTask.FromResult(result);
        }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (mode == "after" && TakeFault())
            {
                Interlocked.Increment(ref _faults);
                throw new AuditStoreException("selected_binding_conflict", 409);
            }
            return Task.CompletedTask;
        }
        public async Task<AuditResult> VerifyAsync(AuditInput input, CancellationToken ct)
        {
            Interlocked.Increment(ref _verifiers);
            if (mode == "post")
            {
                throw new AuditStoreException("selected_binding_conflict", 409);
            }
            if (mode == "transient")
            {
                throw new InvalidOperationException("synthetic_transient_verifier");
            }
            if (mode == "terminal")
            {
                throw new AuditValidationException("incompatible_audit_verifier");
            }
            if (mode == "hold")
            {
                using var registration = ct.Register(() => Cancelled.TrySetResult());
                Entered.TrySetResult();
                await Release.Task;
            }
            return new AuditVerifier().Verify(input);
        }
    }
}
