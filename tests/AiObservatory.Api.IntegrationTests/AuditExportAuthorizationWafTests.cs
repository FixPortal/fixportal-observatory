using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using AiObservatory.Api.Audit;
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
    public async Task Review64_PresentationSpendUsesFrozenSelectedBilledScopeAcrossPages()
    {
        var id = await Seed(aggregateCount: 205, spendCohort: true);
        using var admin = _factory.CreateAdminClient();
        async Task<List<JsonElement>> Drain(string projection)
        {
            var rows = new List<JsonElement>();
            string? cursor = null;
            do
            {
                var page = await admin.GetFromJsonAsync<JsonElement>(
                    $"/api/audits/runs/{id}/evidence?limit=200{projection}"
                        + (cursor == null ? "" : "&cursor=" + cursor),
                    Ct
                );
                rows.AddRange(page.GetProperty("items").EnumerateArray());
                cursor = page.GetProperty("nextCursor").GetString();
            } while (cursor != null);
            return rows;
        }
        var actual = (await Drain("&projection=presentation"))
            .Where(r => r.GetProperty("kind").GetString() == "spend")
            .ToArray();
        actual.Should().HaveCount(2);
        actual
            .Select(r => r.GetProperty("record").GetProperty("currency").GetString())
            .Should()
            .BeEquivalentTo("GBP", "USD");
        actual
            .Select(r => r.GetProperty("record").GetProperty("amountGbp").GetString())
            .Should()
            .BeEquivalentTo("10", "4");
        (await Drain("")).Count(r => r.GetProperty("kind").GetString() == "spend").Should().Be(5);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Review64_ReportsAndSummaryRetainScopeCheckInventoryAndSafeCoverage(bool expired)
    {
        var id = await Seed(
            requestedChecks: [CheckCode.EventAggregation, CheckCode.PricingArithmetic, CheckCode.BillingReconciliation],
            excludedChecks: [CheckCode.DailyAttribution],
            resultTransform: result =>
                result with
                {
                    Checks = result
                        .Checks.Select(c =>
                            c.Code == CheckCode.EventAggregation
                                ? c with
                                {
                                    Verdict = CheckVerdict.Matched,
                                    Coverage = new(
                                        9007199254740993,
                                        9007199254740993,
                                        [new("synthetic", "secret-group", "safe_gap")]
                                    )
                                    {
                                        Exclusions = [new("machineAttribution", "machine_not_in_aggregate_key")],
                                    },
                                }
                                : c
                        )
                        .ToArray(),
                }
        );
        if (expired)
        {
            _clock.Advance(Duration.FromDays(91));
            using var scope = _factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<AuditStore>().ExpireAsync(_clock.GetCurrentInstant(), Ct);
        }
        using var viewer = _factory.CreateReadOnlyClient();
        var report = await viewer.GetFromJsonAsync<JsonElement>($"/api/audits/runs/{id}/report", Ct);
        report.GetProperty("scope").GetProperty("from").GetString().Should().Be("2026-10-20");
        report
            .GetProperty("scope")
            .GetProperty("sources")[0]
            .GetProperty("sourceId")
            .GetString()
            .Should()
            .Be("synthetic");
        report.GetProperty("scope").GetProperty("excludedChecks")[0].GetString().Should().Be("dailyAttribution");
        report.GetProperty("serverCutoff").ValueKind.Should().Be(JsonValueKind.String);
        report.GetProperty("completedAt").ValueKind.Should().Be(JsonValueKind.String);
        report.GetProperty("inputContentDigest").GetString().Should().HaveLength(64);
        var summary = await viewer.GetFromJsonAsync<JsonElement>($"/api/audits/runs/{id}", Ct);
        foreach (var document in new[] { report, summary })
        {
            var checks = document.GetProperty("checks").EnumerateArray().ToArray();
            checks
                .Select(c => c.GetProperty("verdict").GetString())
                .Should()
                .BeEquivalentTo("matched", "incomplete", "unsupported", "excluded");
            var aggregation = checks.Single(c => c.GetProperty("code").GetString() == "eventAggregation");
            aggregation
                .GetProperty("coverage")
                .GetProperty("comparedGroups")
                .GetString()
                .Should()
                .Be("9007199254740993");
            aggregation
                .GetProperty("coverage")
                .GetProperty("exclusions")[0]
                .GetProperty("field")
                .GetString()
                .Should()
                .Be("machineAttribution");
            aggregation.GetProperty("coverage").GetProperty("diagnosticCodes")[0].GetString().Should().Be("safe_gap");
            document
                .GetRawText()
                .Should()
                .NotContain("secret-group")
                .And.NotContain("secret-file")
                .And.NotContain("secret-session");
        }
    }

    [Fact]
    public async Task Retention_AdminOnlyFutureSettings_ViewerReportSurvivesExactDetailExpiry()
    {
        var id = await Seed();
        using var admin = _factory.CreateAdminClient();
        using var viewer = _factory.CreateReadOnlyClient();
        var settings = await admin.GetFromJsonAsync<JsonElement>("/api/audits/retention", Ct);
        settings.GetProperty("evidenceDays").GetInt32().Should().Be(90);
        settings.GetProperty("summaryDays").GetInt32().Should().Be(365);
        (await viewer.GetAsync("/api/audits/retention", Ct)).IsSuccessStatusCode.Should().BeFalse();
        (await viewer.PatchAsJsonAsync("/api/audits/retention", new { evidenceDays = 1, summaryDays = 1 }, Ct))
            .IsSuccessStatusCode.Should()
            .BeFalse();
        (await admin.PatchAsJsonAsync("/api/audits/retention", new { evidenceDays = 36501, summaryDays = 1 }, Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.BadRequest);
        (
            await admin.PatchAsJsonAsync(
                "/api/audits/retention",
                new { evidenceDays = (int?)null, summaryDays = (int?)null },
                Ct
            )
        )
            .StatusCode.Should()
            .Be(HttpStatusCode.OK);
        _clock.Advance(Duration.FromDays(90));
        (await admin.GetAsync($"/api/audits/runs/{id}/bundle", Ct)).StatusCode.Should().Be(HttpStatusCode.Gone);
        var report = await viewer.GetFromJsonAsync<JsonElement>($"/api/audits/runs/{id}/report", Ct);
        report.GetProperty("detailsAvailable").GetBoolean().Should().BeFalse();
        report.GetProperty("originalResultHash").GetString().Should().HaveLength(64);
        report.GetProperty("findings").GetArrayLength().Should().Be(0);
        report.ToString().Should().NotContain("secret-session").And.NotContain("pricingEquation");
        _clock.Advance(Duration.FromDays(275));
        (await viewer.GetAsync($"/api/audits/runs/{id}/report", Ct)).StatusCode.Should().Be(HttpStatusCode.Gone);
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
        foreach (var suffix in new[] { "bundle", "evidence", "evidence?projection=presentation" })
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
    public async Task Evidence_PresentationProjectionDrainsEquivalentRowsAndRejectsForeignCursors()
    {
        var id = await Seed(aggregateCount: 205);
        using var admin = _factory.CreateAdminClient();
        var path = $"/api/audits/runs/{id}/evidence?limit=200";
        var detailed = await admin.GetFromJsonAsync<JsonElement>(path, Ct);
        var projected = await admin.GetFromJsonAsync<JsonElement>(path + "&projection=presentation", Ct);
        var next = projected.GetProperty("nextCursor").GetString()!;
        var tail = await admin.GetFromJsonAsync<JsonElement>(path + "&projection=presentation&cursor=" + next, Ct);
        tail.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
        var rows = projected
            .GetProperty("items")
            .EnumerateArray()
            .Concat(tail.GetProperty("items").EnumerateArray())
            .ToArray();
        rows.Should().HaveCount(205);
        rows.Should().OnlyContain(row => row.GetProperty("kind").GetString() == "aggregate");
        var detailTail = await admin.GetFromJsonAsync<JsonElement>(
            path + "&cursor=" + detailed.GetProperty("nextCursor").GetString(),
            Ct
        );
        var expected = detailed
            .GetProperty("items")
            .EnumerateArray()
            .Concat(detailTail.GetProperty("items").EnumerateArray())
            .Where(row => row.GetProperty("kind").GetString() is "aggregate" or "spend")
            .Select(row => row.GetRawText());
        rows.Select(row => row.GetRawText()).Should().Equal(expected);
        foreach (var field in new[] { "frozenSnapshotHash", "summaryInputDigest" })
        {
            projected.GetProperty(field).GetString().Should().Be(detailed.GetProperty(field).GetString());
            tail.GetProperty(field).GetString().Should().Be(projected.GetProperty(field).GetString());
        }
        (await admin.GetAsync(path + "&cursor=" + next, Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (
            await admin.GetAsync(
                path + "&projection=presentation&cursor=" + detailed.GetProperty("nextCursor").GetString(),
                Ct
            )
        )
            .StatusCode.Should()
            .Be(HttpStatusCode.BadRequest);
        var other = await Seed(aggregateCount: 205);
        (await admin.GetAsync($"/api/audits/runs/{other}/evidence?projection=presentation&cursor={next}", Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.BadRequest);
        foreach (var invalid in new[] { "", "all", "sourceRecord", "presentation&projection=presentation" })
        {
            (await admin.GetAsync(path + "&projection=" + invalid, Ct))
                .StatusCode.Should()
                .Be(HttpStatusCode.BadRequest);
        }
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PresentationProbe_RequiresRequestedNonExcludedParentCheck(bool excluded)
    {
        var id = await Seed([CheckCode.EventAggregation], excluded ? [CheckCode.PresentationTotals] : []);
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<AuditStore>();
        var snapshot = (await store.ReadSnapshotAsync(id, Ct))!;
        var input = AuditCanonicalJson.Deserialize<AuditInput>(snapshot.Bytes);
        var probe = new FrozenPresentationProbe(
            snapshot.Sha256,
            "synthetic-build",
            "synthetic-source",
            AiObservatory.Audit.Checks.PresentationTotalsCheck.SummaryInputDigest(input),
            []
        );
        using var admin = _factory.CreateAdminClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/audits/runs/{id}/presentation-probe")
        {
            Content = new StringContent(
                "{\"schemaVersion\":1,\"probe\":" + Encoding.UTF8.GetString(AuditCanonicalJson.Serialize(probe)) + "}",
                Encoding.UTF8,
                "application/json"
            ),
        };
        request.Headers.Add("Idempotency-Key", "disallowed-probe");
        var response = await admin.SendAsync(request, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("presentation_check_not_requested");
        (await store.ListRunsAsync(null, null, 200, Ct)).Should().ContainSingle();
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
        (await admin.GetAsync($"/api/audits/runs/{id}/evidence?projection=presentation", Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.Conflict);
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

    [Fact]
    public async Task Retention_ActiveExport_DeletionWaitsForCancellationAndReleaseOutsideLock()
    {
        var id = await Seed();
        using var exportScope = _factory.Services.CreateScope();
        var store = exportScope.ServiceProvider.GetRequiredService<AuditStore>();
        var input = AuditCanonicalJson.Deserialize<AuditInput>((await store.ReadSnapshotAsync(id, Ct))!.Bytes);
        var capture = input.Captures.Single().CaptureId;
        var impact = await store.GetDeletionImpactAsync(capture, Ct);
        var temporaryBefore = Directory.GetFiles(Path.GetTempPath(), "aiobs-audit-*.zip");
        await using var output = new ExportGateStream(Ct);
        var exporting = exportScope
            .ServiceProvider.GetRequiredService<AuditReportService>()
            .WriteBundleAsync(id, output, Ct);
        await output.Started.Task.WaitAsync(Ct);
        using var admin = _factory.CreateAdminClient();
        using var deletion = new HttpRequestMessage(HttpMethod.Delete, $"/api/audits/captures/{capture}");
        deletion.Headers.IfMatch.ParseAdd("\"" + impact.ImpactDigest + "\"");
        var deleting = admin.SendAsync(deletion, Ct);
        await output.Cancelled.Task.WaitAsync(Ct);
        try
        {
            deleting
                .IsCompleted.Should()
                .BeFalse("the active writer has observed cancellation but not released its read lease");
            (await admin.GetAsync($"/api/audits/runs/{id}/bundle", Ct)).StatusCode.Should().Be(HttpStatusCode.Gone);
            using var checkScope = _factory.Services.CreateScope();
            var db = checkScope.ServiceProvider.GetRequiredService<AiObservatory.Data.AiObservatoryDbContext>();
            (
                await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(
                    Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.SqlQueryRaw<int>(
                        db.Database,
                        "SELECT count(*)::int AS \"Value\" FROM \"AuditEvidenceChunks\""
                    ),
                    Ct
                )
            )
                .Should()
                .Be(2);
        }
        finally
        {
            output.Release.TrySetResult();
        }
        var failed = async () => await exporting;
        await failed.Should().ThrowAsync<OperationCanceledException>();
        (await deleting).StatusCode.Should().Be(HttpStatusCode.NoContent);
        Directory.GetFiles(Path.GetTempPath(), "aiobs-audit-*.zip").Except(temporaryBefore).Should().BeEmpty();
    }

    [Fact]
    public async Task Retention_PreparedZip_CancelBeforeHttpExecution_ImmediatelyRemovesSpoolAndReleasesLease()
    {
        var id = await Seed();
        using var scope = _factory.Services.CreateScope();
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var session = await scope
            .ServiceProvider.GetRequiredService<AuditReportService>()
            .OpenAsync(id, requestCancellation.Token);
        await using var zip = await AuditReportService.PrepareBundleAsync(session);
        var path = zip.Name;
        File.Exists(path).Should().BeTrue();
        var result = new AuditLeasedHttpResult(
            session,
            Microsoft.AspNetCore.Http.Results.Stream(zip, "application/zip", "synthetic-audit.zip")
        );
        await requestCancellation.CancelAsync();
        await result.ExecuteAsync(
            new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = scope.ServiceProvider }
        );
        var db = scope.ServiceProvider.GetRequiredService<AiObservatory.Data.AiObservatoryDbContext>();
        (
            await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(
                Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.SqlQueryRaw<int>(
                    db.Database,
                    "SELECT count(*)::int AS \"Value\" FROM \"AuditExportReadLeases\""
                ),
                Ct
            )
        )
            .Should()
            .Be(0);
        File.Exists(path)
            .Should()
            .BeFalse("the HTTP wrapper owns private evidence even when its inner result never executes");
    }

    private sealed class ExportGateStream(CancellationToken testToken) : Stream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            using var registration = cancellationToken.Register(() => Cancelled.TrySetResult());
            Started.TrySetResult();
            await Release.Task.WaitAsync(testToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private async Task<Guid> Seed(
        CheckCode[]? requestedChecks = null,
        CheckCode[]? excludedChecks = null,
        int aggregateCount = 0,
        bool spendCohort = false,
        Func<AuditResult, AuditResult>? resultTransform = null
    )
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
                requestedChecks ?? [CheckCode.PresentationTotals],
                excludedChecks ?? [],
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
            Aggregates = Enumerable
                .Range(0, aggregateCount)
                .Select(index => new FrozenAggregate(
                    new(
                        new(2026, 10, 20),
                        "OpenAi",
                        $"synthetic-model-{index:D4}",
                        "synthetic",
                        SourceKind.Synthetic,
                        UsageScope.Subscription,
                        AccountingBasis.Notional
                    ),
                    1,
                    0,
                    0,
                    0,
                    0,
                    4m,
                    0,
                    0m,
                    0,
                    1
                ))
                .ToArray(),
        };
        if (spendCohort)
        {
            FrozenSpend Spend(string source, AccountingBasis basis, string currency, decimal gbp, int day) =>
                new(
                    Guid.NewGuid(),
                    source,
                    null,
                    null,
                    null,
                    machine.MachineId,
                    machine.MachineId,
                    new(2026, 10, day),
                    null,
                    null,
                    new(SourceKind.Manual, UsageScope.Api, basis),
                    currency,
                    gbp,
                    gbp,
                    1m,
                    null,
                    timestamp,
                    timestamp,
                    new('a', 64)
                );
            input = input with
            {
                Spend =
                [
                    Spend("synthetic", AccountingBasis.Billed, "GBP", 10m, 20),
                    Spend("synthetic", AccountingBasis.Billed, "USD", 4m, 20),
                    Spend("other", AccountingBasis.Billed, "GBP", 20m, 20),
                    Spend("synthetic", AccountingBasis.Notional, "GBP", 30m, 20),
                    Spend("synthetic", AccountingBasis.Billed, "GBP", 40m, 19),
                ],
            };
        }
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
            $"export-fixture-{machine.MachineId:D}",
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
        var result = new AuditVerifier().Verify(input);
        (await store.TryPublishAsync(lease!, resultTransform?.Invoke(result) ?? result, Ct)).Should().BeTrue();
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
