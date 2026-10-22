namespace AiObservatory.Api.Audit;

public static class AuditEvidenceEndpoints
{
    public static void MapAuditEvidenceEndpoints(this WebApplication app) =>
        AuditExportEndpoints
            .Admin(app)
            .MapGet(
                "/{id:guid}/evidence",
                async (Guid id, HttpRequest request, AuditReportService service, CancellationToken ct) =>
                {
                    var session = await service.OpenAsync(id, ct);
                    try
                    {
                        var document = AuditReportService.Evidence(session.Materialized);
                        var bundle = document.Bundle;
                        var evidence = new List<object>();
                        void Add<T>(string kind, IReadOnlyList<T> rows) =>
                            evidence.AddRange(rows.Select(record => AuditViewerProjection.Wire(new { kind, record })));
                        Add("capture", bundle.Input.Captures);
                        Add("pricingCatalog", bundle.Input.PricingCatalogs);
                        Add("sourceRecord", bundle.Input.Records);
                        Add("event", bundle.Input.Events);
                        Add("aggregate", bundle.Input.Aggregates);
                        Add("spend", bundle.Input.Spend);
                        Add("sourceStatus", bundle.Input.SourceStatuses);
                        Add("fx", bundle.Input.FxRates);
                        Add("financialObservation", bundle.Input.FinancialObservations ?? []);
                        Add("sourceQualification", bundle.Input.SourceQualifications ?? []);
                        if (bundle.Input.PresentationProbe != null)
                        {
                            Add("presentationProbe", new[] { bundle.Input.PresentationProbe });
                        }
                        Add("finding", bundle.Result.Findings);
                        Add("pricingEquation", bundle.Result.Checks.SelectMany(c => c.PricingTraces ?? []).ToArray());
                        var page = AuditViewerProjection.Wire(
                            AuditViewerProjection.Page(evidence, request, bundle.Result.InputContentDigest)
                        );
                        page["frozenSnapshotHash"] = document.FrozenSnapshotHash;
                        page["summaryInputDigest"] = document.SummaryInputDigest;
                        session.Token.ThrowIfCancellationRequested();
                        return new AuditLeasedHttpResult(session, Results.Ok(page));
                    }
                    catch
                    {
                        await session.DisposeAsync();
                        throw;
                    }
                }
            );
}
