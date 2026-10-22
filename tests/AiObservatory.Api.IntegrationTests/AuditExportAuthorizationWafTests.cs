using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using AiObservatory.Audit;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using AiObservatory.Data.Audit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NodaTime.Testing;

namespace AiObservatory.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class AuditExportAuthorizationWafTests : IAsyncLifetime
{
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 10, 22, 12, 0));
    private readonly AiObservatoryApiFactory _factory;
    private readonly CancellationTokenSource _ceiling = CancellationTokenSource.CreateLinkedTokenSource(
        TestContext.Current.CancellationToken
    );
    private CancellationToken Ct => _ceiling.Token;

    public AuditExportAuthorizationWafTests()
    {
        _factory = new() { ClockOverride = _clock };
        _factory.ConfigurationOverrides["Audit:Enabled"] = "true";
    }

    public async ValueTask InitializeAsync()
    {
        _ceiling.CancelAfter(TimeSpan.FromSeconds(45));
        await _factory.InitializeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        _ceiling.Dispose();
    }

    [Fact]
    public async Task ViewerCanReadSanitizedFindingsAndReport_ButCannotExportPrivateEvidence()
    {
        var id = await Seed();
        using var viewer = _factory.CreateReadOnlyClient();
        foreach (var suffix in new[] { "findings", "report" })
        {
            var response = await viewer.GetAsync($"/api/audits/runs/{id}/{suffix}", Ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await response.Content.ReadAsStringAsync(Ct);
            body.Should().NotContain("secret-session").And.NotContain("evidenceRefs");
        }
        foreach (var suffix in new[] { "bundle", "evidence" })
        {
            (await viewer.GetAsync($"/api/audits/runs/{id}/{suffix}", Ct))
                .StatusCode.Should()
                .Be(HttpStatusCode.Unauthorized);
        }
        using var admin = _factory.CreateAdminClient();
        var first = await admin.GetByteArrayAsync($"/api/audits/runs/{id}/bundle", Ct);
        var second = await admin.GetByteArrayAsync($"/api/audits/runs/{id}/bundle", Ct);
        first.Should().Equal(second);
        using var export = new MemoryStream(first);
        var restored = await AiObservatory.Audit.Bundles.AuditBundleReader.ReadBundleAsync(export, new(), Ct);
        restored.Captures.Single().Chunks.Should().HaveCount(2);
        restored.Captures[0].Chunks[0].Ndjson.Should().Equal(restored.Captures[0].Chunks[1].Ndjson);
        restored.Captures[0].Manifest.RecordCount.Should().Be(2);
        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/audits/runs/{id}/evidence", Ct);
        detail.GetProperty("frozenSnapshotHash").GetString().Should().HaveLength(64);
        detail
            .GetProperty("summaryInputDigest")
            .GetString()
            .Should()
            .Be(AiObservatory.Audit.Checks.PresentationTotalsCheck.SummaryInputDigest(restored.Input));
        var posting = detail
            .GetProperty("items")
            .EnumerateArray()
            .Single(i => i.GetProperty("kind").GetString() == "event")
            .GetProperty("record");
        posting.GetProperty("tokens").GetProperty("inputTokens").GetString().Should().Be("9007199254740993");
        posting.GetProperty("costUsd").GetString().Should().Be("2");
        posting.GetProperty("provenance").GetProperty("costBasis").GetString().Should().Be("notional");
        (await admin.GetAsync($"/api/audits/runs/{id}/evidence?limit=201", Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.BadRequest);
        _clock.Advance(Duration.FromDays(91));
        (await admin.GetAsync($"/api/audits/runs/{id}/bundle", Ct)).StatusCode.Should().Be(HttpStatusCode.Gone);
    }

    [Fact]
    public async Task ViewerKeyDominatesEntra_ForPrivilegedDetailsAndProbe()
    {
        await using var authenticated = _factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<IStartupFilter, AuthenticatedStartup>())
        );
        using var viewer = authenticated.CreateClient();
        viewer.DefaultRequestHeaders.Add("X-Observatory-Key", AiObservatoryApiFactory.ReadOnlyKey);
        foreach (var suffix in new[] { "bundle", "evidence" })
        {
            (await viewer.GetAsync($"/api/audits/runs/{Guid.NewGuid()}/{suffix}", Ct))
                .StatusCode.Should()
                .Be(HttpStatusCode.Forbidden);
        }
        (await viewer.PostAsync($"/api/audits/runs/{Guid.NewGuid()}/presentation-probe", null, Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PresentationProbe_ValidatesHashesAndCreatesImmutableReplayableEvaluation()
    {
        var id = await Seed();
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<AuditStore>();
        var original = (await store.ReadSnapshotAsync(id, Ct))!;
        var input = AuditCanonicalJson.Deserialize<AuditInput>(original.Bytes);
        var values = AiObservatory
            .Audit.Checks.PresentationTotalsCheck.Reference(input)
            .Select(v => v.Field == "notionalUsd" ? v with { UnroundedValue = 10m, FormattedValue = "10.00" } : v)
            .ToArray();
        var probe = new FrozenPresentationProbe(
            original.Sha256,
            "claimed-build",
            "claimed-source",
            AiObservatory.Audit.Checks.PresentationTotalsCheck.SummaryInputDigest(input),
            values
        );
        using var admin = _factory.CreateAdminClient();
        async Task<HttpResponseMessage> Post(FrozenPresentationProbe observation, string key)
        {
            var json =
                "{\"schemaVersion\":1,\"probe\":"
                + Encoding.UTF8.GetString(AuditCanonicalJson.Serialize(observation))
                + "}";
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/audits/runs/{id}/presentation-probe")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("Idempotency-Key", key);
            return await admin.SendAsync(request, Ct);
        }
        (await Post(probe with { FrozenSnapshotHash = new('b', 64) }, "bad"))
            .StatusCode.Should()
            .Be(HttpStatusCode.Conflict);
        (await Post(probe with { SummaryInputDigest = new('b', 64) }, "bad-summary"))
            .StatusCode.Should()
            .Be(HttpStatusCode.Conflict);
        var response = await Post(probe, "probe");
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var linked = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("runId").GetGuid();
        var retry = await Post(probe, "probe");
        (await retry.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("runId").GetGuid().Should().Be(linked);
        var frozen = (await store.ReadSnapshotAsync(linked, Ct))!;
        var evaluation = AuditCanonicalJson.Deserialize<AuditInput>(frozen.Bytes);
        evaluation.PresentationProbe!.FrontendBuildId.Should().Be("claimed-build");
        new AuditVerifier()
            .Verify(evaluation)
            .Findings.Should()
            .Contain(f => f.RuleCode == "presentation_totals_difference");
        (await store.ReadSnapshotAsync(id, Ct))!.Bytes.Should().Equal(original.Bytes);
        (await store.GetRunAsync(linked, Ct))!.EvaluationOfRunId.Should().Be(id);
        (await Post(probe with { FrontendBuildId = "different" }, "probe"))
            .StatusCode.Should()
            .Be(HttpStatusCode.Conflict);
        var lease = await store.TryLeaseRunAsync(Guid.NewGuid(), _clock.GetCurrentInstant(), Ct);
        (await store.TryPublishAsync(lease!, new AuditVerifier().Verify(evaluation), Ct)).Should().BeTrue();
        using var bytes = new MemoryStream(await admin.GetByteArrayAsync($"/api/audits/runs/{linked}/bundle", Ct));
        var offline = await AiObservatory.Audit.Bundles.AuditBundleReader.ReadBundleAsync(bytes, new(), Ct);
        AuditCanonicalJson
            .Serialize(new AuditVerifier().Verify(offline.Input))
            .Should()
            .Equal(AuditCanonicalJson.Serialize(offline.Result));
    }

    [Fact]
    public async Task AlteredRetainedSnapshot_IsRejectedBeforeExport()
    {
        var id = await Seed();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AiObservatory.Data.AiObservatoryDbContext>();
        await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlInterpolatedAsync(
            db.Database,
            $"UPDATE \"AuditServerSnapshots\" SET \"Sha256\"={new string('b', 64)} WHERE \"RunId\"={id}",
            Ct
        );
        using var admin = _factory.CreateAdminClient();
        (await admin.GetAsync($"/api/audits/runs/{id}/bundle", Ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await admin.GetAsync($"/api/audits/runs/{id}/evidence", Ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task DeletedCapture_RefusesNewExportsWhileSummaryRemainsReadable()
    {
        var id = await Seed();
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<AuditStore>();
        var input = AuditCanonicalJson.Deserialize<AuditInput>((await store.ReadSnapshotAsync(id, Ct))!.Bytes);
        var captureId = input.Captures.Single().CaptureId;
        var impact = await store.CaptureDeletionImpactAsync(captureId, Ct);
        await store.DeleteCaptureAsync(captureId, impact, Ct);
        using var admin = _factory.CreateAdminClient();
        (await admin.GetAsync($"/api/audits/runs/{id}/bundle", Ct)).StatusCode.Should().Be(HttpStatusCode.Gone);
        (await admin.GetAsync($"/api/audits/runs/{id}/evidence", Ct)).StatusCode.Should().Be(HttpStatusCode.Gone);
        using var viewer = _factory.CreateReadOnlyClient();
        (await viewer.GetAsync($"/api/audits/runs/{id}/report", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CompletedRun_CoverageCountsAreInvariantDecimalStrings()
    {
        var id = await Seed();
        using var viewer = _factory.CreateReadOnlyClient();
        var run = await viewer.GetFromJsonAsync<JsonElement>($"/api/audits/runs/{id}", Ct);
        var coverage = run.GetProperty("coverage");
        coverage.GetProperty("comparedGroups").ValueKind.Should().Be(JsonValueKind.String);
        coverage.GetProperty("totalGroups").ValueKind.Should().Be(JsonValueKind.String);
        long.TryParse(
                coverage.GetProperty("totalGroups").GetString(),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var total
            )
            .Should()
            .BeTrue();
        total.Should().BeGreaterThan(0);
    }

    private async Task<Guid> Seed()
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<AuditStore>();
        var machine = await store.RegisterMachineAsync(new byte[32], Ct);
        var input = new AuditInput
        {
            SchemaVersion = 1,
            VerifierVersion = AuditVerifierIdentity.VerifierVersion,
            RuleVersion = AuditVerifierIdentity.RuleVersion,
            Scope = new(
                new(2026, 10, 20),
                new(2026, 10, 20),
                [new(machine.MachineId, "synthetic")],
                [CheckCode.PresentationTotals],
                [],
                true
            ),
        };
        var timestamp = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        input = input with
        {
            Events =
            [
                new(
                    Guid.NewGuid(),
                    "secret-event",
                    "synthetic",
                    new(SourceKind.Synthetic, UsageScope.Subscription, AccountingBasis.Notional),
                    timestamp,
                    timestamp,
                    timestamp,
                    "OpenAi",
                    "model",
                    new(9007199254740993, 0, 0, 0, 0, 0),
                    2m,
                    0m,
                    null,
                    "secret-session",
                    null,
                    null,
                    new('a', 64),
                    null
                ),
            ],
        };
        var source = new SourceUsageRecord
        {
            MachineId = machine.MachineId,
            SourceId = "synthetic",
            SourceFormat = "codex-jsonl",
            SourceVersion = "1",
            Tool = SourceTool.Codex,
            Kind = UsageRecordKind.CumulativeUsage,
            Generation = "g1",
            FileId = "secret-file",
            SessionId = "secret-session",
            Sequence = 1,
            Timestamp = timestamp,
            Provider = "OpenAi",
            Model = "model",
            Counters = new CodexCounters(9007199254740993, 0, 0, 0, 0),
            Pricing = new(ServiceTier.Standard, false, null, null),
            Closure = new(ClosureState.Unknown, null, null),
        };
        var chunk = AuditCanonicalJson.Serialize(source).Concat(new byte[] { 10 }).ToArray();
        var hash = AuditCanonicalJson.ContentHash(chunk);
        await store.CreateCaptureRequestAsync(machine.MachineId, input.Scope, _clock.GetCurrentInstant(), Ct);
        var captureLease = (
            await store.TryClaimCaptureAsync(machine.MachineId, machine.CredentialId, _clock.GetCurrentInstant(), Ct)
        )!;
        await store.PutChunkAsync(captureLease, 0, chunk, hash, Ct);
        await store.PutChunkAsync(captureLease, 1, chunk, hash, Ct);
        var now = _clock.GetCurrentInstant().ToDateTimeOffset();
        var manifest = new CaptureManifest(
            1,
            Guid.NewGuid(),
            captureLease.RequestId,
            machine.MachineId,
            "g1",
            "1",
            now,
            now,
            input.Scope,
            [new("synthetic", "codex-jsonl", "1")],
            1,
            1,
            0,
            0,
            2,
            new(2026, 10, 20),
            new(2026, 10, 20),
            [],
            [hash, hash],
            now
        );
        var captureId = await store.SealCaptureAsync(captureLease, AuditCanonicalJson.Serialize(manifest), "seal", Ct);
        input = input with { Captures = [manifest], Records = [source] };
        var id = await store.CreateRunAsync(
            input.Scope,
            [captureId],
            "export-fixture",
            false,
            _clock.GetCurrentInstant(),
            Ct
        );
        var lease = await store.TryLeaseRunAsync(Guid.NewGuid(), _clock.GetCurrentInstant(), Ct);
        (
            await store.TryStoreSnapshotAsync(
                lease!,
                AuditCanonicalJson.Serialize(input),
                AuditComputationInput.ContentDigest(input),
                Ct
            )
        )
            .Should()
            .BeTrue();
        (await store.TryPublishAsync(lease!, new AuditVerifier().Verify(input), Ct)).Should().BeTrue();
        return id;
    }

    private sealed class AuthenticatedStartup : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(
                    async (c, invoke) =>
                    {
                        c.User = new ClaimsPrincipal(
                            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "human")], "synthetic-entra")
                        );
                        await invoke();
                    }
                );
                next(app);
            };
    }
}
