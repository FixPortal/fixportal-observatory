using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using AiObservatory.Data;
using AiObservatory.Data.Audit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;
using NodaTime.Testing;
using AuditOptions = AiObservatory.Api.Audit.AuditOptions;

namespace AiObservatory.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class AuditCaptureEndpointsWafTests : IAsyncLifetime
{
    private static readonly Instant Now = Instant.FromUtc(2026, 10, 22, 12, 0);
    private readonly FakeClock _clock = new(Now);
    private readonly AiObservatoryApiFactory _factory;
    private readonly CancellationTokenSource _ceiling = CancellationTokenSource.CreateLinkedTokenSource(
        TestContext.Current.CancellationToken
    );
    private CancellationToken Ct => _ceiling.Token;
    private readonly ConcurrentQueue<string> _logs = new();

    public AuditCaptureEndpointsWafTests()
    {
        _factory = new() { ClockOverride = _clock };
        _factory.ConfigurationOverrides["Audit:Enabled"] = "true";
    }

    public async ValueTask InitializeAsync()
    {
        _ceiling.CancelAfter(TimeSpan.FromSeconds(30));
        await _factory.InitializeAsync();
        _factory.Services.GetRequiredService<ILoggerFactory>().AddProvider(new CaptureLogs(_logs));
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        _ceiling.Dispose();
    }

    [Fact]
    public async Task Provision_Rotate_Revoke_IsAdminOnlyAndSecretIsScoped()
    {
        using var viewer = _factory.CreateReadOnlyClient();
        (await viewer.PostAsJsonAsync("/api/audits/machines", new { schemaVersion = 1 }, Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.Unauthorized);
        var issued = await Issue();
        Convert.FromHexString(issued.Secret.Split('.')[1]).Length.Should().Be(32);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            var stored = await db
                .Database.SqlQueryRaw<byte[]>("SELECT \"SecretHash\" AS \"Value\" FROM \"AuditMachineCredentials\"")
                .SingleAsync(Ct);
            stored.Should().Equal(SHA256.HashData(Convert.FromHexString(issued.Secret.Split('.')[1])));
        }
        using var machine = Machine(issued.Secret);
        using var impersonator = _factory.CreateAnonymousClient();
        impersonator.DefaultRequestHeaders.Add("X-Observatory-Key", issued.Secret);
        (await impersonator.PostAsJsonAsync("/api/events", new { }, Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.Unauthorized);
        using var ordinaryAsMachine = Machine(AiObservatoryApiFactory.AdminKey);
        (await Claim(ordinaryAsMachine)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await machine.PostAsJsonAsync("/api/events", new { }, Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await machine.PostAsJsonAsync("/api/audits/machines", new { schemaVersion = 1 }, Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.Unauthorized);
        using var admin = _factory.CreateAdminClient();
        (await admin.PostAsJsonAsync("/api/audits/capture-requests/claim", new { schemaVersion = 1 }, Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.Unauthorized);
        var rotatedResponse = await admin.PostAsJsonAsync(
            $"/api/audits/machines/{issued.MachineId}/rotate",
            new { schemaVersion = 1 },
            Ct
        );
        rotatedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var rotated = await rotatedResponse.Content.ReadFromJsonAsync<JsonElement>(Ct);
        rotated.GetProperty("secret").GetString().Should().NotBe(issued.Secret);
        (await Claim(machine)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var replacement = Machine(rotated.GetProperty("secret").GetString()!);
        (await Claim(replacement)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await admin.DeleteAsync($"/api/audits/machines/{issued.MachineId}/credential", Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.NoContent);
        (await Claim(replacement)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        string.Join('\n', _logs).Should().NotContain(issued.Secret).And.NotContain(issued.Secret.Split('.')[1]);
    }

    [Fact]
    public async Task Revocation_DuringUnsealedLease_RefusesBothUploadAndSeal()
    {
        var issued = await Issue();
        await Request(issued.MachineId);
        using var machine = Machine(issued.Secret);
        var lease = await Lease(machine);
        using var admin = _factory.CreateAdminClient();
        await admin.DeleteAsync($"/api/audits/machines/{issued.MachineId}/credential", Ct);
        (await Put(machine, lease, Record(issued.MachineId))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Seal(machine, lease, [], 0)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var wrongSecret = Machine(issued.Secret[..^1] + (issued.Secret[^1] == 'a' ? 'b' : 'a'));
        (await Claim(wrongSecret)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Provisioning_ReadOnlyKeyDominatesSimultaneousAuthenticatedHuman()
    {
        await using var authenticated = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddSingleton<IStartupFilter, SyntheticAuthenticatedStartup>()
            )
        );
        using var viewer = authenticated.CreateClient();
        viewer.DefaultRequestHeaders.Add("X-Observatory-Key", AiObservatoryApiFactory.ReadOnlyKey);
        (await viewer.PostAsJsonAsync("/api/audits/machines", new { schemaVersion = 1 }, Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.Forbidden);
        var issued = await Issue();
        (await viewer.PostAsJsonAsync($"/api/audits/machines/{issued.MachineId}/rotate", new { schemaVersion = 1 }, Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.Forbidden);
        (await viewer.DeleteAsync($"/api/audits/machines/{issued.MachineId}/credential", Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.Forbidden);
        using var machine = Machine(issued.Secret);
        (await Claim(machine)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var human = authenticated.CreateClient();
        (await human.PostAsJsonAsync("/api/audits/machines", new { schemaVersion = 1 }, Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.OK);
    }

    private sealed class SyntheticAuthenticatedStartup : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(
                    async (context, invokeNext) =>
                    {
                        context.User = new ClaimsPrincipal(
                            new ClaimsIdentity(
                                [new Claim(ClaimTypes.NameIdentifier, "synthetic-human")],
                                "synthetic-entra"
                            )
                        );
                        await invokeNext();
                    }
                );
                next(app);
            };
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CaptureBudgets_EnforceAggregateBytesAndRecordCountAtConfiguredBoundary(bool countLimit)
    {
        var issued = await Issue();
        await Request(issued.MachineId);
        var bytes = Record(issued.MachineId);
        await using var limited = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.PostConfigure<AuditOptions>(options =>
                {
                    options.MaxChunkBytes = bytes.Length;
                    options.MaxCaptureBytes = countLimit ? bytes.Length * 4 : bytes.Length;
                    options.MaxRecordsPerCapture = countLimit ? 1 : 1_000_000;
                })
            )
        );
        using var machine = limited.CreateClient();
        machine.DefaultRequestHeaders.Add("X-Observatory-Audit-Key", issued.Secret);
        var lease = await Lease(machine);
        (await Put(machine, lease, bytes)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Put(machine, lease, bytes)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Put(machine, lease, bytes, index: 1)).StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        if (!countLimit)
        {
            (await Seal(machine, lease, [AuditCanonicalJson.ContentHash(bytes)], 1))
                .StatusCode.Should()
                .Be(HttpStatusCode.RequestEntityTooLarge);
        }
    }

    [Fact]
    public async Task Upload_Seal_RetriesPreserveOriginalBytes_AndRevocationIsImmediate()
    {
        var issued = await Issue();
        var request = await Request(issued.MachineId);
        using var machine = Machine(issued.Secret);
        var lease = await Lease(machine);
        lease.RequestId.Should().Be(request);
        var bytes = Record(issued.MachineId);
        (await Put(machine, lease, bytes)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Put(machine, lease, bytes)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Put(machine, lease, Record(issued.MachineId, "different")))
            .StatusCode.Should()
            .Be(HttpStatusCode.Conflict);
        (await Put(machine, lease with { Token = Guid.NewGuid() }, bytes))
            .StatusCode.Should()
            .Be(HttpStatusCode.Conflict);
        var sealedResponse = await Seal(machine, lease, [AuditCanonicalJson.ContentHash(bytes)], 1);
        sealedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Seal(machine, lease, [AuditCanonicalJson.ContentHash(bytes)], 1))
            .StatusCode.Should()
            .Be(HttpStatusCode.OK);
        using var admin = _factory.CreateAdminClient();
        await admin.DeleteAsync($"/api/audits/machines/{issued.MachineId}/credential", Ct);
        (await Put(machine, lease, bytes)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Seal(machine, lease, [], 0)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>().UsageEvents.CountAsync(Ct))
            .Should()
            .Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProtocolCeilings_AcceptExactCaptureBudgetAndRejectNextRecord(bool recordCount)
    {
        var issued = await Issue();
        await Request(issued.MachineId);
        using var machine = Machine(issued.Secret);
        var lease = await Lease(machine);
        var bytes = Record(issued.MachineId);
        // Seed only the durable counters near the protocol ceiling; avoid a half-GiB/million-record fixture.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            long uploaded = recordCount ? 0 : 512L * 1024 * 1024 - bytes.Length;
            long records = recordCount ? 999_999 : 0;
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"AuditCaptureRequests\" SET \"UploadedBytes\"={uploaded}, \"RecordCount\"={records} WHERE \"Id\"={lease.RequestId}",
                Ct
            );
        }
        (await Put(machine, lease, bytes)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Put(machine, lease, bytes, index: 1)).StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task Demo_BlocksMachineAndProvisioningMutations()
    {
        await using var factory = new AiObservatoryApiFactory { DemoMode = true };
        factory.ConfigurationOverrides["Audit:Enabled"] = "true";
        using var admin = factory.CreateAdminClient();
        (await admin.PostAsJsonAsync("/api/audits/machines", new { schemaVersion = 1 }, Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.Forbidden);
        (await admin.PostAsJsonAsync("/api/audits/capture-requests/claim", new { schemaVersion = 1 }, Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CrossMachine_UnknownAndForeignRequestsHaveSameResponse()
    {
        var owner = await Issue();
        var other = await Issue();
        await Request(owner.MachineId);
        using var ownClient = Machine(owner.Secret);
        using var otherClient = Machine(other.Secret);
        (await Claim(otherClient)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var lease = await Lease(ownClient);
        (await Put(otherClient, lease, Record(owner.MachineId))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Put(otherClient, lease with { RequestId = Guid.NewGuid() }, Record(owner.MachineId)))
            .StatusCode.Should()
            .Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ExpiredOwnership_ResetsUnsealedChunks_AndFencesOldToken()
    {
        var issued = await Issue();
        await Request(issued.MachineId);
        using var machine = Machine(issued.Secret);
        var old = await Lease(machine);
        (await Put(machine, old, Record(issued.MachineId, "old"))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Lease(machine)).Token.Should().Be(old.Token);
        _clock.Advance(Duration.FromMinutes(6));
        // Each HTTP call resolves an independent DbContext, including this takeover.
        var next = await Lease(machine);
        next.Token.Should().NotBe(old.Token);
        var bytes = Record(issued.MachineId, "new");
        (await Put(machine, next, bytes)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Put(machine, old, bytes)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        var response = await Seal(machine, next, [AuditCanonicalJson.ContentHash(bytes)], 1);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("captureId").GetGuid();
        using var scope = _factory.Services.CreateScope();
        var retained = await Store(scope).ReadCaptureAsync(id, Ct);
        retained!.Chunks.Should().ContainSingle().Which.Should().Equal(bytes);
    }

    [Fact]
    public async Task ExpiredRequest_CannotUploadOrSeal()
    {
        var issued = await Issue();
        await Request(issued.MachineId);
        using var machine = Machine(issued.Secret);
        var lease = await Lease(machine);
        _clock.Advance(Duration.FromDays(1));
        (await Put(machine, lease, Record(issued.MachineId))).StatusCode.Should().Be(HttpStatusCode.Gone);
        (await Seal(machine, lease, [], 0)).StatusCode.Should().Be(HttpStatusCode.Gone);
        (await Claim(machine)).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task SensitiveEnvelopes_RejectUnknownFieldsAndBadHashes()
    {
        using var admin = _factory.CreateAdminClient();
        (await admin.PostAsJsonAsync("/api/audits/machines", new { schemaVersion = 1, path = "private" }, Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.BadRequest);
        var issued = await Issue();
        await Request(issued.MachineId);
        using var machine = Machine(issued.Secret);
        (
            await machine.PostAsJsonAsync(
                "/api/audits/capture-requests/claim",
                new { schemaVersion = 1, url = "https://evil.test" },
                Ct
            )
        )
            .StatusCode.Should()
            .Be(HttpStatusCode.BadRequest);
        var lease = await Lease(machine);
        (await Put(machine, lease, Record(issued.MachineId), hash: new string('0', 64)))
            .StatusCode.Should()
            .Be(HttpStatusCode.BadRequest);
        var bad = new HttpRequestMessage(HttpMethod.Post, $"/api/audits/capture-requests/{lease.RequestId}/seal")
        {
            Content = JsonContent.Create(new { schemaVersion = 1, command = "private" }),
        };
        bad.Headers.Add("X-Observatory-Audit-Lease", lease.Token.ToString());
        bad.Headers.Add("Idempotency-Key", "seal");
        var response = await machine.SendAsync(bad, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().NotContain("private");
        (await Seal(machine, lease, [], 0, prettyManifest: true)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ChunkBoundary_RejectsAboveEightMiB()
    {
        var issued = await Issue();
        await Request(issued.MachineId);
        using var machine = Machine(issued.Secret);
        var lease = await Lease(machine);
        (await Put(machine, lease, new byte[8 * 1024 * 1024 + 1]))
            .StatusCode.Should()
            .Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task ChunkBoundary_AcceptsExactlyEightMiBOfCanonicalRecords()
    {
        var issued = await Issue();
        await Request(issued.MachineId);
        using var machine = Machine(issued.Secret);
        var lease = await Lease(machine);
        var line = Record(issued.MachineId);
        const int size = 8 * 1024 * 1024;
        var repetitions = size / line.Length - 1;
        var padding = size - (repetitions + 1) * line.Length;
        var first = Math.Min(padding, 254);
        var second = Math.Min(padding - first, 254);
        var third = padding - first - second;
        var last = AuditCanonicalJson.Deserialize<SourceUsageRecord>(line.AsSpan(0, line.Length - 1)) with
        {
            Model = new string('m', first + 2),
            Provider = new string('p', second + 2),
            AgentId = new string('a', third + 2),
        };
        byte[] tail = [.. AuditCanonicalJson.Serialize(last), 10];
        var bytes = new byte[size];
        for (var index = 0; index < repetitions; index++)
        {
            line.CopyTo(bytes, index * line.Length);
        }
        tail.CopyTo(bytes, repetitions * line.Length);
        (await Put(machine, lease, bytes)).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DisabledFeature_HidesProvisionAndClaim()
    {
        await using var factory = new AiObservatoryApiFactory();
        factory.ConfigurationOverrides["Audit:MaxChunkBytes"] = "-1";
        using var admin = factory.CreateAdminClient();
        (await admin.PostAsJsonAsync("/api/audits/machines", new { schemaVersion = 1 }, Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.NotFound);
        (await admin.PostAsJsonAsync("/api/audits/capture-requests/claim", new { schemaVersion = 1 }, Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.NotFound);
        using var anonymous = factory.CreateAnonymousClient();
        (await anonymous.PostAsJsonAsync("/api/audits/machines", new { schemaVersion = 1 }, Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.NotFound);
    }

    private async Task<(Guid MachineId, string Secret)> Issue()
    {
        using var admin = _factory.CreateAdminClient();
        var response = await admin.PostAsJsonAsync("/api/audits/machines", new { schemaVersion = 1 }, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return (json.GetProperty("machineId").GetGuid(), json.GetProperty("secret").GetString()!);
    }

    private HttpClient Machine(string secret)
    {
        var client = _factory.CreateAnonymousClient();
        client.DefaultRequestHeaders.Add("X-Observatory-Audit-Key", secret);
        return client;
    }

    private Task<HttpResponseMessage> Claim(HttpClient client) =>
        client.PostAsJsonAsync("/api/audits/capture-requests/claim", new { schemaVersion = 1 }, Ct);

    private async Task<CaptureLease> Lease(HttpClient client)
    {
        var response = await Claim(client);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var machineId = json.GetProperty("machineId").GetGuid();
        return new(
            json.GetProperty("requestId").GetGuid(),
            machineId,
            Guid.Empty,
            json.GetProperty("leaseToken").GetGuid(),
            _clock.GetCurrentInstant().Plus(Duration.FromMinutes(5)),
            Scope(machineId)
        );
    }

    private async Task<Guid> Request(Guid machine)
    {
        using var scope = _factory.Services.CreateScope();
        return await Store(scope).CreateCaptureRequestAsync(machine, Scope(machine), _clock.GetCurrentInstant(), Ct);
    }

    private AuditStore Store(IServiceScope scope) =>
        new(scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>(), _clock);

    private static AuditScope Scope(Guid machine) =>
        new(new(2026, 10, 1), new(2026, 10, 7), [new(machine, "claude-local")], [CheckCode.SourceAccounting], [], true);

    private static byte[] Record(Guid machine, string? model = null) =>
        [
            .. AuditCanonicalJson.Serialize(
                new SourceUsageRecord
                {
                    MachineId = machine,
                    SourceId = "claude-local",
                    SourceFormat = "claude-jsonl",
                    SourceVersion = "1",
                    Tool = SourceTool.Claude,
                    Kind = UsageRecordKind.SessionMetadata,
                    Generation = "generation-a",
                    FileId = "file-a",
                    Sequence = 1,
                    Model = model,
                    Timestamp = Now.ToDateTimeOffset(),
                    Pricing = new(ServiceTier.Unknown, false, null, null),
                    Closure = new(ClosureState.Unknown, null, null),
                }
            ),
            10,
        ];

    private Task<HttpResponseMessage> Put(
        HttpClient client,
        CaptureLease lease,
        byte[] bytes,
        string? hash = null,
        int index = 0
    )
    {
        var message = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/audits/capture-requests/{lease.RequestId}/chunks/{index}"
        )
        {
            Content = new ByteArrayContent(bytes),
        };
        message.Headers.Add("X-Observatory-Audit-Lease", lease.Token.ToString());
        message.Headers.Add("X-Content-SHA256", hash ?? AuditCanonicalJson.ContentHash(bytes));
        return client.SendAsync(message, Ct);
    }

    private sealed class CaptureLogs(ConcurrentQueue<string> entries) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(entries);

        public void Dispose() { }

        private sealed class CaptureLogger(ConcurrentQueue<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter
            ) => entries.Enqueue(formatter(state, exception));
        }
    }

    private Task<HttpResponseMessage> Seal(
        HttpClient client,
        CaptureLease lease,
        IReadOnlyList<string> hashes,
        long count,
        bool prettyManifest = false
    )
    {
        var manifest = new CaptureManifest(
            1,
            lease.RequestId,
            lease.RequestId,
            lease.MachineId,
            "generation-a",
            "1",
            Now.ToDateTimeOffset(),
            Now.ToDateTimeOffset(),
            lease.Scope,
            [new("claude-local", "claude-jsonl", "1")],
            1,
            1,
            0,
            0,
            count,
            null,
            null,
            [],
            hashes,
            Now.ToDateTimeOffset()
        );
        var bytes = AuditCanonicalJson.Serialize(manifest);
        var manifestText = Encoding.UTF8.GetString(bytes);
        if (prettyManifest)
        {
            manifestText = "{ " + manifestText[1..];
        }
        var body =
            $"{{\"schemaVersion\":1,\"manifest\":{manifestText},\"sha256\":\"{AuditCanonicalJson.ContentHash(bytes)}\"}}";
        var message = new HttpRequestMessage(HttpMethod.Post, $"/api/audits/capture-requests/{lease.RequestId}/seal")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        message.Headers.Add("X-Observatory-Audit-Lease", lease.Token.ToString());
        message.Headers.Add("Idempotency-Key", "seal");
        return client.SendAsync(message, Ct);
    }
}
