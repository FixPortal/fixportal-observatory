using AiObservatory.Data.Audit;

namespace AiObservatory.Api.Audit;

public static class AuditExportEndpoints
{
    public static RouteGroupBuilder Admin(WebApplication app) =>
        app.MapGroup("/api/audits/runs")
            .RequireRateLimiting("api")
            .AddEndpointFilter<AuditProtocolFilter>()
            .AddEndpointFilter<AuditAdminAuthorityFilter>()
            .AddEndpointFilter<ApiKeyEndpointFilter>()
            .AddEndpointFilter<AdminOnlyApiKeyEndpointFilter>();

    public static void MapAuditExportEndpoints(this WebApplication app)
    {
        var viewer = app.MapGroup("/api/audits/runs")
            .RequireRateLimiting("api")
            .AddEndpointFilter<AuditProtocolFilter>()
            .AddEndpointFilter<ApiKeyEndpointFilter>();
        viewer.MapGet(
            "/{id:guid}/findings",
            async (Guid id, HttpRequest request, AuditStore store, CancellationToken ct) =>
            {
                var result = await AuditViewerProjection.ResultAsync(store, id, ct);
                var items = AuditViewerProjection.Findings(result);
                return Results.Ok(AuditViewerProjection.Page(items, request, result.InputContentDigest));
            }
        );
        viewer.MapGet(
            "/{id:guid}/report",
            async (Guid id, AuditStore store, CancellationToken ct) =>
            {
                var run = await store.ReadReportAsync(id, ct);
                var result = AuditViewerProjection.FromRun(run);
                var report = AuditViewerProjection.Wire(
                    new
                    {
                        schemaVersion = 1,
                        detailsAvailable = run.Result != null,
                        originalResultHash = run.OriginalResultHash,
                        summaryKind = run.Result != null ? "detailedProjection" : "retainedSanitizedSummary",
                        result.Scope,
                        result.InputContentDigest,
                        serverCutoff = run.ServerCutoff?.ToDateTimeOffset(),
                        completedAt = run.CompletedAt?.ToDateTimeOffset(),
                        observationContext = run.ServerCutoff == null ? "unavailable" : "serverSnapshotCutoff",
                        result.Verdict,
                        result.VerifierVersion,
                        result.RuleVersion,
                        coverage = AuditViewerProjection.Coverage(result.Coverage, run.Result != null),
                        checks = AuditViewerProjection.Checks(result, run.Result != null),
                        findings = AuditViewerProjection.Findings(result),
                    }
                );
                return Results.File(
                    System.Text.Encoding.UTF8.GetBytes(
                        report.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true })
                    ),
                    "application/json",
                    $"audit-{id:D}-report.json"
                );
            }
        );
        Admin(app)
            .MapGet(
                "/{id:guid}/bundle",
                async (Guid id, AuditReportService service, CancellationToken ct) =>
                {
                    var session = await service.OpenBundleAsync(id, ct);
                    try
                    {
                        var zip = await AuditReportService.PrepareBundleAsync(session);
                        return new AuditLeasedHttpResult(
                            session,
                            Results.Stream(zip, "application/zip", $"audit-{id:D}.zip")
                        );
                    }
                    catch
                    {
                        await session.DisposeAsync();
                        throw;
                    }
                }
            );
    }
}
