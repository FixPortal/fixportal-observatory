using System.Text;
using AiObservatory.Audit.Serialization;
using AiObservatory.Data.Audit;

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
                    var presentation = request.Query.ContainsKey("projection");
                    if (
                        presentation
                        && (request.Query["projection"].Count != 1 || request.Query["projection"] != "presentation")
                    )
                    {
                        throw new AuditStoreException("invalid_evidence_projection", 400);
                    }
                    var session = await service.OpenAsync(id, ct);
                    try
                    {
                        var document = AuditReportService.Evidence(session.Materialized);
                        var bundle = document.Bundle;
                        var evidence = new List<object>();
                        void Add<T>(string kind, IReadOnlyList<T> rows) =>
                            evidence.AddRange(rows.Select(record => AuditViewerProjection.Wire(new { kind, record })));
                        if (!presentation)
                        {
                            Add("capture", bundle.Input.Captures);
                            Add("pricingCatalog", bundle.Input.PricingCatalogs);
                            Add("sourceRecord", bundle.Input.Records);
                            Add("event", bundle.Input.Events);
                        }
                        Add("aggregate", bundle.Input.Aggregates);
                        Add("spend", bundle.Input.Spend);
                        if (!presentation)
                        {
                            Add("sourceStatus", bundle.Input.SourceStatuses);
                            Add("fx", bundle.Input.FxRates);
                            Add("financialObservation", bundle.Input.FinancialObservations ?? []);
                            Add("sourceQualification", bundle.Input.SourceQualifications ?? []);
                            if (bundle.Input.PresentationProbe != null)
                            {
                                Add("presentationProbe", new[] { bundle.Input.PresentationProbe });
                            }
                            Add("finding", bundle.Result.Findings);
                            Add(
                                "pricingEquation",
                                bundle.Result.Checks.SelectMany(c => c.PricingTraces ?? []).ToArray()
                            );
                        }
                        // Bind offsets to this run, immutable scope/input, snapshot and projection.
                        var binding = AuditCanonicalJson.ContentHash(
                            Encoding.UTF8.GetBytes(
                                $"{id:D}:{bundle.Result.InputContentDigest}:{document.FrozenSnapshotHash}:{(presentation ? "presentation" : "detailed")}"
                            )
                        );
                        var page = AuditViewerProjection.Wire(AuditViewerProjection.Page(evidence, request, binding));
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
