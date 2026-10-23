using System.Net;
using System.Text.Json;
using AiObservatory.Api.Audit;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using AiObservatory.Data;
using AiObservatory.Data.Audit;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using NodaTime;
using NodaTime.Testing;
using AuditOptions = AiObservatory.Api.Audit.AuditOptions;

namespace AiObservatory.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed partial class AuditSelectedInvocationEndpointsWafTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 10, 23, 12, 0);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RealProgramUnboundOrAuditOffIsPrivate404(bool audit, bool bound)
    {
        await using var factory = Factory(audit);
        if (bound)
        {
            Bind(factory, Guid.NewGuid(), Scope());
        }
        await factory.InitializeAsync();
        using var client = factory.CreateAdminClient();
        using var response = await client.GetAsync(AuditSelectedInvocationEndpoints.BindingRoute, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task RealProgramUsesExactReadOnlyStoreAndFrozenStartupAuthority()
    {
        await using var factory = Factory(true);
        var configuredId = Guid.NewGuid();
        var scope = Scope();
        Bind(factory, configuredId, scope);
        await factory.InitializeAsync();
        var ct = TestContext.Current.CancellationToken;
        await using var services = factory.Services.CreateAsyncScope();
        var store = services.ServiceProvider.GetRequiredService<AuditStore>();
        var db = services.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        var created = await store.CreateRunAsync(scope, [], "selected-metadata-waf", false, Now, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"AuditRuns\" SET \"Id\"={configuredId},\"Revision\"=9,\"Attempts\"=1,\"State\"='failed',\"Stage\"='failed' WHERE \"Id\"={created}", ct);
        await store.CreateRunAsync(scope, [], "selected-unrelated-waf", false, Now, ct);
        var before = await Facts(db, ct);
        var configured = factory.Services.GetRequiredService<IOptions<AuditSelectedInvocationOptions>>().Value;
        configured.RunId = Guid.NewGuid().ToString("D");
        configured.FullScopeDigest = new string('b', 64);
        configured.ExpectedRevision = "999";
        using var admin = factory.CreateAdminClient();
        using var response = await admin.GetAsync(AuditSelectedInvocationEndpoints.BindingRoute, ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        bytes.Length.Should().BeLessThanOrEqualTo(32 * 1024);
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        root.GetProperty("schemaVersion").GetInt32().Should().Be(1);
        root.GetProperty("runId").GetGuid().Should().Be(configuredId);
        root.GetProperty("fullScopeDigest").GetString().Should().Be(AuditCanonicalJson.ContentHash(AuditCanonicalJson.Serialize(scope)));
        root.GetProperty("expectedRevision").GetInt64().Should().Be(0);
        root.GetProperty("currentRevision").GetInt64().Should().Be(9);
        root.GetProperty("state").GetString().Should().Be("failed");
        root.GetProperty("resultAvailable").GetBoolean().Should().BeFalse();
        root.GetProperty("captureRequestIds").GetArrayLength().Should().Be(0);
        root.GetProperty("captureIds").GetArrayLength().Should().Be(0);
        root.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "schemaVersion", "runId", "invocationId", "fullScopeDigest", "expectedRevision", "currentRevision",
            "state", "stage", "attempts", "captureRequestIds", "requests", "captureIds", "evidenceExpiresAt",
            "summaryExpiresAt", "diagnosticCode", "resultAvailable");
        (await Facts(db, ct)).Should().Be(before);
        var options = factory.Services.GetRequiredService<IOptions<AuditOptions>>().Value;
        options.WorkerEnabled.Should().BeFalse();
        factory.Services.GetRequiredService<IOptions<LoggerFilterOptions>>().Value.Rules.Should()
            .Contain(rule => rule.CategoryName == "Microsoft.AspNetCore" && rule.LogLevel == LogLevel.Warning);
        using var viewer = factory.CreateReadOnlyClient();
        using var denied = await viewer.GetAsync(AuditSelectedInvocationEndpoints.BindingRoute, ct);
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        denied.Headers.CacheControl!.NoStore.Should().BeTrue();
        (await Facts(db, ct)).Should().Be(before);
    }

    [Fact]
    public async Task RealProgramCorsPreflightStillReceivesPrefixPrivacy()
    {
        await using var factory = Factory(false);
        await factory.InitializeAsync();
        using var client = factory.CreateAnonymousClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, AuditSelectedInvocationEndpoints.BindingRoute);
        request.Headers.Add("Origin", "https://example.test");
        request.Headers.Add("Access-Control-Request-Method", "GET");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Theory]
    [InlineData("RunId", "PRIVATE_GUID_SENTINEL")]
    [InlineData("ExpectedRevision", "PRIVATE_REVISION_SENTINEL")]
    [InlineData("ExpiresAtUtc", "PRIVATE_EXPIRY_SENTINEL")]
    public async Task RealProgramInvalidBindingFailsWithoutEchoingValues(string field, string value)
    {
        await using var factory = Factory(true);
        Bind(factory, Guid.NewGuid(), Scope());
        factory.ConfigurationOverrides[AuditSelectedInvocationOptions.SectionName + ":" + field] = value;
        var action = async () => await factory.InitializeAsync();
        var exception = (await action.Should().ThrowAsync<Exception>()).Which;
        exception.ToString().Should().Contain(field).And.NotContain(value);
    }

    private static AiObservatoryApiFactory Factory(bool audit)
    {
        var factory = new AiObservatoryApiFactory { ClockOverride = new FakeClock(Now) };
        factory.ConfigurationOverrides["Audit:Enabled"] = audit ? "true" : "false";
        factory.ConfigurationOverrides["Audit:WorkerEnabled"] = "false";
        return factory;
    }

    private static AuditScope Scope() => new(new(2026, 10, 1), new(2026, 10, 7),
        [new(Guid.NewGuid(), "claude-local")], [CheckCode.SourceAccounting], [], true);

    private static void Bind(AiObservatoryApiFactory factory, Guid runId, AuditScope scope)
    {
        var prefix = AuditSelectedInvocationOptions.SectionName + ":";
        factory.ConfigurationOverrides[prefix + "RunId"] = runId.ToString("D");
        factory.ConfigurationOverrides[prefix + "InvocationId"] = Guid.NewGuid().ToString("D");
        factory.ConfigurationOverrides[prefix + "FullScopeDigest"] = AuditCanonicalJson.ContentHash(AuditCanonicalJson.Serialize(scope));
        factory.ConfigurationOverrides[prefix + "ExpectedRevision"] = "0";
        factory.ConfigurationOverrides[prefix + "ExpiresAtUtc"] = "2020-01-02T03:04:00Z";
        factory.ConfigurationOverrides[prefix + "Enabled"] = "false";
    }

    private static Task<string> Facts(AiObservatoryDbContext db, CancellationToken ct) => db.Database.SqlQueryRaw<string>("""
        SELECT jsonb_build_object(
          'runs',(SELECT jsonb_agg(to_jsonb(r) ORDER BY r."Id") FROM "AuditRuns" r),
          'requests',(SELECT jsonb_agg(to_jsonb(r) ORDER BY r."Id") FROM "AuditCaptureRequests" r),
          'links',(SELECT jsonb_agg(to_jsonb(r) ORDER BY r."RunId",r."CaptureId") FROM "AuditRunCaptures" r),
          'captures',(SELECT jsonb_agg(to_jsonb(r) ORDER BY r."Id") FROM "AuditEvidenceCaptures" r),
          'snapshots',(SELECT jsonb_agg(to_jsonb(r) ORDER BY r."Id") FROM "AuditServerSnapshots" r),
          'policy',(SELECT jsonb_agg(to_jsonb(r) ORDER BY r."InstanceId") FROM "AuditRetentionSettings" r)
        )::text AS "Value"
        """).SingleAsync(ct);
}
