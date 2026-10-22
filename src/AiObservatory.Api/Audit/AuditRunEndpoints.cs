using System.Globalization;
using System.Text;
using System.Text.Json;
using AiObservatory.Audit;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using AiObservatory.Audit.Sources;
using AiObservatory.Data.Audit;
using NodaTime;

namespace AiObservatory.Api.Audit;

public static class AuditRunEndpoints
{
    public static void MapAuditRunEndpoints(this WebApplication app)
    {
        var reads = app.MapGroup("/api/audits")
            .RequireRateLimiting("api")
            .AddEndpointFilter<AuditProtocolFilter>()
            .AddEndpointFilter<ApiKeyEndpointFilter>();
        reads.MapGet("/capabilities", () => Results.Ok(Capabilities()));
        reads.MapGet(
            "/readiness",
            async (HttpRequest request, AuditReadinessService service, CancellationToken ct) =>
            {
                var from = Date(request.Query["from"].ToString());
                var to = Date(request.Query["to"].ToString());
                var sources = request
                    .Query["sources"]
                    .ToString()
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (sources.Length is 0 or > 32)
                {
                    throw new AuditStoreException("invalid_source_selection", 400);
                }
                return Results.Ok(
                    await service.GetAsync(
                        new(
                            from,
                            to,
                            sources.Distinct().Select(s => new SourceSelection(Guid.Empty, s)).ToArray(),
                            [CheckCode.SourceAccounting],
                            [],
                            false
                        ),
                        ct
                    )
                );
            }
        );
        reads.MapGet(
            "/runs",
            async (HttpRequest request, AuditStore store, CancellationToken ct) =>
            {
                var limit = 50;
                if (request.Query.ContainsKey("limit") && !int.TryParse(request.Query["limit"], out limit))
                {
                    throw new AuditStoreException("invalid_page_limit", 400);
                }
                var (before, id) = ParseCursor(request.Query["cursor"].ToString());
                var runs = await store.ListRunsAsync(before, id, limit, ct);
                var last = runs.LastOrDefault();
                return Results.Ok(
                    new
                    {
                        schemaVersion = 1,
                        items = runs.Select(Summary),
                        nextCursor = runs.Count == limit && last != null ? Cursor(last) : null,
                    }
                );
            }
        );
        reads.MapGet(
            "/runs/{id:guid}",
            async (Guid id, AuditStore store, CancellationToken ct) =>
            {
                var run = await store.GetRunAsync(id, ct);
                return run == null ? Results.NotFound() : Results.Ok(Summary(run));
            }
        );
        var writes = app.MapGroup("/api/audits/runs")
            .RequireRateLimiting("api")
            .AddEndpointFilter<AuditProtocolFilter>()
            .AddEndpointFilter<AuditAdminAuthorityFilter>()
            .AddEndpointFilter<ApiKeyEndpointFilter>()
            .AddEndpointFilter<AdminOnlyApiKeyEndpointFilter>();
        writes.MapPost(
            "",
            async (HttpRequest request, AuditRunService service, CancellationToken ct) =>
            {
                using var doc = JsonDocument.Parse(await AuditCaptureProtocol.ReadAsync(request, 64 * 1024, ct));
                var root = doc.RootElement;
                ValidateEnvelope(root);
                return await Create(request, root, service, ct);
            }
        );
        writes.MapPost(
            "/{id:guid}/cancel",
            async (Guid id, AuditStore store, CancellationToken ct) =>
            {
                await store.CancelRunAsync(id, ct);
                return Results.NoContent();
            }
        );
        writes.MapPost(
            "/{id:guid}/proceed",
            async (Guid id, AuditStore store, CancellationToken ct) =>
            {
                await store.ProceedRunAsync(id, ct);
                return Results.NoContent();
            }
        );
        writes.MapPost(
            "/{id:guid}/replay",
            async (Guid id, HttpRequest request, AuditRunService service, CancellationToken ct) =>
            {
                await ValidateReplayRequest(request, ct);
                var run = await service.ReplayAsync(id, request.Headers["Idempotency-Key"].ToString(), ct);
                return Results.Accepted($"/api/audits/runs/{run}", new { schemaVersion = 1, runId = run });
            }
        );
    }

    private static object Capabilities() =>
        new
        {
            schemaVersion = 1,
            enabled = true,
            schemaVersions = new[] { 1 },
            verifierVersions = new[] { AuditVerifierIdentity.VerifierVersion },
            sources = AuditCapabilities.Sources,
            checks = Enum.GetValues<CheckCode>()
                .Select(c => new
                {
                    code = c,
                    supported = c
                        is CheckCode.SourceAccounting
                            or CheckCode.DailyAttribution
                            or CheckCode.EventAggregation
                            or CheckCode.PresentationTotals
                            or CheckCode.PricingArithmetic,
                    reasonCode = c is CheckCode.BillingReconciliation ? "billing_feed_unqualified" : null,
                }),
        };

    private static async Task ValidateReplayRequest(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength is null or 0 && !request.Headers.ContainsKey("Transfer-Encoding"))
        {
            return;
        }
        using var doc = JsonDocument.Parse(await AuditCaptureProtocol.ReadAsync(request, 4096, ct));
        var root = doc.RootElement;
        if (
            root.ValueKind != JsonValueKind.Object
            || root.EnumerateObject().Count() != 2
            || !root.TryGetProperty("schemaVersion", out var schema)
            || schema.ValueKind != JsonValueKind.Number
            || !schema.TryGetInt32(out var value)
            || value != 1
            || !root.TryGetProperty("verifierVersion", out var verifier)
            || verifier.ValueKind != JsonValueKind.String
        )
        {
            throw new AuditStoreException("invalid_audit_request", 400);
        }
        if (verifier.GetString() != AuditVerifierIdentity.VerifierVersion)
        {
            throw new AuditStoreException("unsupported_replay_version", 409);
        }
    }

    private static void ValidateEnvelope(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new AuditStoreException("invalid_audit_request", 400);
        }
        var names = root.EnumerateObject().Select(p => p.Name).ToArray();
        if (
            names.Length != 4
            || names.Distinct().Count() != 4
            || names.Except(["schemaVersion", "scope", "captureIds", "requestFreshCapture"]).Any()
        )
        {
            throw new AuditStoreException("invalid_audit_request", 400);
        }
        if (
            root.GetProperty("schemaVersion").ValueKind != JsonValueKind.Number
            || !root.GetProperty("schemaVersion").TryGetInt32(out _)
            || root.GetProperty("scope").ValueKind != JsonValueKind.Object
            || root.GetProperty("captureIds").ValueKind != JsonValueKind.Array
            || root.GetProperty("requestFreshCapture").ValueKind is not (JsonValueKind.True or JsonValueKind.False)
        )
        {
            throw new AuditStoreException("invalid_audit_request", 400);
        }
        if (root.GetProperty("captureIds").GetArrayLength() > 200)
        {
            throw new AuditStoreException("capture_selection_limit", 413);
        }
    }

    private static async Task<IResult> Create(
        HttpRequest request,
        JsonElement root,
        AuditRunService service,
        CancellationToken ct
    )
    {
        if (root.GetProperty("schemaVersion").GetInt32() != 1)
        {
            throw new AuditStoreException("unsupported_schema", 400);
        }
        var scope = AuditCanonicalJson.Deserialize<AuditScope>(
            Encoding.UTF8.GetBytes(root.GetProperty("scope").GetRawText())
        );
        var captures =
            root.GetProperty("captureIds").Deserialize<Guid[]>()
            ?? throw new AuditStoreException("invalid_capture_selection", 400);
        var id = await service.CreateAsync(
            new(scope, captures, root.GetProperty("requestFreshCapture").GetBoolean()),
            request.Headers["Idempotency-Key"].ToString(),
            ct
        );
        return Results.Accepted($"/api/audits/runs/{id}", new { schemaVersion = 1, runId = id });
    }

    private static object Summary(AuditRunView run)
    {
        var resultBytes = run.Result ?? run.SanitizedSummary;
        var result = resultBytes == null ? null : AuditCanonicalJson.Deserialize<AuditResult>(resultBytes);
        return new
        {
            schemaVersion = 1,
            run.Id,
            detailsAvailable = run.Result != null,
            replayAvailable = run.State == "completed" && run.Result != null,
            state = run.State == "executing" || run.State == "collecting" ? run.Stage : run.State,
            stage = run.Stage,
            run.Attempts,
            run.DiagnosticCode,
            createdAt = run.CreatedAt.ToDateTimeOffset(),
            scope = AuditCanonicalJson.Deserialize<AuditScope>(run.Scope),
            verdict = result?.Verdict,
            coverage = result == null
                ? null
                : new
                {
                    comparedGroups = result.Coverage.ComparedGroups.ToString(CultureInfo.InvariantCulture),
                    totalGroups = result.Coverage.TotalGroups.ToString(CultureInfo.InvariantCulture),
                    diagnosticCodes = result.Coverage.Gaps.Select(g => g.DiagnosticCode).Distinct(),
                },
            result?.VerifierVersion,
            result?.RuleVersion,
            result?.InputContentDigest,
            serverCutoff = run.ServerCutoff?.ToDateTimeOffset(),
            replayExpiresAt = run.EvidenceExpiresAt?.ToDateTimeOffset(),
            evidenceExpiresAt = run.EvidenceExpiresAt?.ToDateTimeOffset(),
            summaryExpiresAt = run.SummaryExpiresAt?.ToDateTimeOffset(),
            run.PredecessorRunId,
            run.EvaluationOfRunId,
        };
    }

    private static DateOnly Date(string text) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new AuditStoreException("invalid_scope_dates", 400);

    private static string Cursor(AuditRunView run) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{run.CreatedAt.ToUnixTimeTicks()}|{run.Id:D}"));

    private static (Instant?, Guid?) ParseCursor(string text)
    {
        if (text.Length == 0)
        {
            return (null, null);
        }
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(text)).Split('|');
            return (
                Instant.FromUnixTimeTicks(long.Parse(parts[0], CultureInfo.InvariantCulture)),
                Guid.Parse(parts[1])
            );
        }
        catch (Exception ex)
            when (ex is FormatException or OverflowException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            throw new AuditStoreException("invalid_cursor", 400);
        }
    }
}
