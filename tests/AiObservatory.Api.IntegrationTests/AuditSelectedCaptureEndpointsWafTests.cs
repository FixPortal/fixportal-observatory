using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiObservatory.Api.Audit;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using AiObservatory.Data;
using AiObservatory.Data.Audit;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using ApiAuditOptions = AiObservatory.Api.Audit.AuditOptions;

namespace AiObservatory.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class AuditSelectedCaptureEndpointsWafTests
{
    private const string Prefix = "/api/audits/capture-requests/selected/";
    private static readonly Instant Now = Instant.FromUtc(2026, 10, 24, 12, 0);

    [Fact]
    public async Task SelectedCapture_ExactMachineCanClaimAndRenewWithExecutionDisabled()
    {
        var clock = new FakeClock(Now);
        await using var factory = new AiObservatoryApiFactory { ClockOverride = clock };
        var machineId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var scope = new AuditScope(
            new(2026, 10, 21),
            new(2026, 10, 21),
            [new(machineId, "synthetic-claude"), new(machineId, "synthetic-codex")],
            [CheckCode.SourceAccounting],
            [],
            false
        );
        var digest = AuditCanonicalJson.ContentHash(AuditCanonicalJson.Serialize(scope));
        factory.ConfigurationOverrides["Audit:Enabled"] = "true";
        factory.ConfigurationOverrides["AuditSelectedInvocation:Enabled"] = "false";
        factory.ConfigurationOverrides["AuditSelectedInvocation:RunId"] = runId.ToString("D");
        factory.ConfigurationOverrides["AuditSelectedInvocation:InvocationId"] = Guid.NewGuid().ToString("D");
        factory.ConfigurationOverrides["AuditSelectedInvocation:FullScopeDigest"] = digest;
        factory.ConfigurationOverrides["AuditSelectedInvocation:ExpectedRevision"] = "0";
        factory.ConfigurationOverrides["AuditSelectedInvocation:ExpiresAtUtc"] = "2026-10-24T12:03:00Z";
        await factory.InitializeAsync();
        var ct = TestContext.Current.CancellationToken;
        await using var services = factory.Services.CreateAsyncScope();
        var db = services.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        var store = services.ServiceProvider.GetRequiredService<AuditStore>();
        var credentialId = Guid.NewGuid();
        var secretBytes = RandomNumberGenerator.GetBytes(32);
        var hash = SHA256.HashData(secretBytes);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO \"AuditMachines\" (\"Id\",\"CreatedAt\") VALUES ({machineId},{Now})",
            ct
        );
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO \"AuditMachineCredentials\" (\"Id\",\"MachineId\",\"SecretHash\",\"CreatedAt\") VALUES ({credentialId},{machineId},{hash},{Now})",
            ct
        );
        var created = await store.CreateRunAsync(scope, [], "selected-capture-http", true, Now, ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"AuditRuns\" SET \"Id\"={runId} WHERE \"Id\"={created}",
            ct
        );
        var requestId = await db
            .Database.SqlQuery<Guid>(
                $"SELECT \"Id\" AS \"Value\" FROM \"AuditCaptureRequests\" WHERE \"MachineId\"={machineId}"
            )
            .SingleAsync(ct);
        using var client = factory.CreateAnonymousClient();
        client.DefaultRequestHeaders.Add(
            "X-Observatory-Audit-Key",
            $"{credentialId:D}.{Convert.ToHexString(secretBytes).ToLowerInvariant()}"
        );
        var body = new
        {
            schemaVersion = 1,
            runId,
            expectedRevision = 0,
            fullScopeDigest = digest,
            requestId,
            machineId,
            requestScopeDigest = digest,
        };
        var before = await HttpFacts(db, ct);
        await AssertTupleRefusalsAsync(client, body, db, before, ct);
        await AssertMalformedClaimRefusalsAsync(client, body, db, before, ct);
        await AssertAuthenticationAndEnvelopeRefusalsAsync(factory, client, body, machineId, ct);
        var auditOptions = factory.Services.GetRequiredService<IOptions<ApiAuditOptions>>().Value;
        // Hosted services already observed their startup-disabled setting. This changes only
        // the synthetic endpoint option to exercise its independent off-worker admission gate.
        auditOptions.WorkerEnabled = true;
        try
        {
            using var workerOn = await client.PostAsJsonAsync(Prefix + "claim", body, ct);
            workerOn.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }
        finally
        {
            auditOptions.WorkerEnabled = false;
        }
        (await HttpFacts(db, ct)).Should().Be(before);
        // Mutable options cannot retarget the startup-frozen authority.
        factory.Services.GetRequiredService<IOptions<AuditSelectedInvocationOptions>>().Value.RunId = Guid.NewGuid()
            .ToString("D");
        using var claim = await client.PostAsJsonAsync(Prefix + "claim", body, ct);
        claim.StatusCode.Should().Be(HttpStatusCode.OK);
        claim.Headers.CacheControl!.NoStore.Should().BeTrue();
        var lease = await claim.Content.ReadFromJsonAsync<JsonElement>(ct);
        lease.GetProperty("runId").GetGuid().Should().Be(runId);
        lease.GetProperty("requestId").GetGuid().Should().Be(requestId);
        lease
            .GetProperty("expiresAt")
            .GetDateTimeOffset()
            .Should()
            .Be(Now.Plus(Duration.FromMinutes(3)).ToDateTimeOffset());
        lease.GetProperty("serverObservedAt").GetDateTimeOffset().Should().Be(Now.ToDateTimeOffset());
        client.DefaultRequestHeaders.Add("X-Observatory-Audit-Lease", lease.GetProperty("leaseToken").GetString());
        using var renewal = await client.PostAsJsonAsync(Prefix + "renew", body, ct);
        renewal.StatusCode.Should().Be(HttpStatusCode.OK);
        var renewed = await renewal.Content.ReadFromJsonAsync<JsonElement>(ct);
        renewed.GetProperty("leaseToken").GetGuid().Should().Be(lease.GetProperty("leaseToken").GetGuid());
        (await store.GetRunAsync(runId, ct))!.Revision.Should().Be(0);
        await AssertRenewalRefusalsAsync(client, body, db, store, clock, lease, credentialId, ct);
    }

    private static async Task AssertTupleRefusalsAsync(
        HttpClient client,
        object body,
        AiObservatoryDbContext db,
        string before,
        CancellationToken ct
    )
    {
        foreach (
            var field in new[]
            {
                "runId",
                "requestId",
                "machineId",
                "expectedRevision",
                "fullScopeDigest",
                "requestScopeDigest",
            }
        )
        {
            var invalid = JsonSerializer.SerializeToNode(body)!.AsObject();
            invalid[field] = field switch
            {
                "expectedRevision" => JsonValue.Create(1),
                "fullScopeDigest" or "requestScopeDigest" => JsonValue.Create(new string('f', 64)),
                _ => JsonValue.Create(Guid.NewGuid().ToString("D")),
            };
            using var refusal = await client.PostAsJsonAsync(Prefix + "claim", invalid, ct);
            refusal.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await HttpFacts(db, ct)).Should().Be(before);
        }
    }

    private static async Task AssertMalformedClaimRefusalsAsync(
        HttpClient client,
        object body,
        AiObservatoryDbContext db,
        string before,
        CancellationToken ct
    )
    {
        var validJson = JsonSerializer.Serialize(body);
        foreach (
            var raw in new[]
            {
                validJson.Replace("\"schemaVersion\":1", "\"schemaVersion\":\"1\""),
                validJson.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1"),
                validJson[..^1] + ",\"unknown\":true}",
                validJson.Replace("\"expectedRevision\":0", "\"expectedRevision\":9223372036854775808"),
            }
        )
        {
            using var content = new StringContent(raw, Encoding.UTF8, "application/json");
            using var refusal = await client.PostAsync(Prefix + "claim", content, ct);
            refusal.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await HttpFacts(db, ct)).Should().Be(before);
        }
    }

    private static async Task AssertAuthenticationAndEnvelopeRefusalsAsync(
        AiObservatoryApiFactory factory,
        HttpClient client,
        object body,
        Guid machineId,
        CancellationToken ct
    )
    {
        using (var query = await client.PostAsJsonAsync(Prefix + "claim?other=1", body, ct))
        {
            query.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        using (var anonymous = factory.CreateAnonymousClient())
        using (var refused = await anonymous.PostAsJsonAsync(Prefix + "claim", body, ct))
        {
            refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        using (var admin = factory.CreateAdminClient())
        using (var refused = await admin.PostAsJsonAsync(Prefix + "claim", body, ct))
        {
            refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        using var oversizedContent = new StringContent(new string(' ', 1025), Encoding.UTF8, "application/json");
        using (var oversized = await client.PostAsync(Prefix + "claim", oversizedContent, ct))
        {
            oversized.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        }
        var uppercase = JsonSerializer.SerializeToNode(body)!.AsObject();
        uppercase["machineId"] = machineId.ToString("D").ToUpperInvariant();
        using var noncanonical = await client.PostAsJsonAsync(Prefix + "claim", uppercase, ct);
        noncanonical.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static async Task AssertRenewalRefusalsAsync(
        HttpClient client,
        object body,
        AiObservatoryDbContext db,
        AuditStore store,
        FakeClock clock,
        JsonElement lease,
        Guid credentialId,
        CancellationToken ct
    )
    {
        var uploading = await HttpFacts(db, ct);
        client.DefaultRequestHeaders.Remove("X-Observatory-Audit-Lease");
        using (var missing = await client.PostAsJsonAsync(Prefix + "renew", body, ct))
        {
            missing.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        client.DefaultRequestHeaders.Add("X-Observatory-Audit-Lease", Guid.NewGuid().ToString("D"));
        using (var wrong = await client.PostAsJsonAsync(Prefix + "renew", body, ct))
        {
            wrong.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }
        (await HttpFacts(db, ct)).Should().Be(uploading);
        client.DefaultRequestHeaders.Remove("X-Observatory-Audit-Lease");
        client.DefaultRequestHeaders.Add("X-Observatory-Audit-Lease", lease.GetProperty("leaseToken").GetString());
        clock.Advance(Duration.FromMinutes(4));
        using (var expired = await client.PostAsJsonAsync(Prefix + "renew", body, ct))
        {
            expired.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }
        (await HttpFacts(db, ct)).Should().Be(uploading);
        await store.RevokeCredentialAsync(credentialId, ct);
        using (var revoked = await client.PostAsJsonAsync(Prefix + "renew", body, ct))
        {
            revoked.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        (await HttpFacts(db, ct)).Should().Be(uploading);
    }

    private static async Task<string> HttpFacts(AiObservatoryDbContext db, CancellationToken ct) =>
        string.Join(
            "\n",
            await db
                .Database.SqlQueryRaw<string>(
                    """
                    SELECT row_to_json(t)::text AS "Value" FROM (
                        SELECT 'request' AS kind,row_to_json(r)::text AS data FROM "AuditCaptureRequests" r
                        UNION ALL SELECT 'run',row_to_json(r)::text FROM "AuditRuns" r
                        UNION ALL SELECT 'chunk',row_to_json(c)::text FROM "AuditEvidenceChunks" c
                    ) t ORDER BY kind,data
                    """
                )
                .ToArrayAsync(ct)
        );

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectedCapture_UnboundNeverOffersMachineCapture(bool audit)
    {
        await using var factory = new AiObservatoryApiFactory();
        factory.ConfigurationOverrides["Audit:Enabled"] = audit.ToString();
        await factory.InitializeAsync();
        using var client = factory.CreateAnonymousClient();
        using var response = await client.PostAsJsonAsync(
            Prefix + "claim",
            new { schemaVersion = 1 },
            TestContext.Current.CancellationToken
        );
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
