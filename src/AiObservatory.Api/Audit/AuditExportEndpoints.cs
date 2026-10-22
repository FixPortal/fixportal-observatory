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
                var result = await AuditViewerProjection.ResultAsync(store, id, ct);
                var report = AuditViewerProjection.Wire(
                    new
                    {
                        schemaVersion = 1,
                        result.Verdict,
                        result.VerifierVersion,
                        result.RuleVersion,
                        checks = result.Checks.Select(c => new
                        {
                            c.Code,
                            c.Verdict,
                            c.Coverage.ComparedGroups,
                            c.Coverage.TotalGroups,
                            diagnosticCodes = c.Coverage.Gaps.Select(g => g.DiagnosticCode).Distinct(),
                        }),
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
                    using var output = new MemoryStream();
                    await service.WriteBundleAsync(id, output, ct);
                    return Results.File(output.ToArray(), "application/zip", $"audit-{id:D}.zip");
                }
            );
    }
}
