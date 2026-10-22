using System.Text;
using System.Text.Json;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using AiObservatory.Data.Audit;

namespace AiObservatory.Api.Audit;

public static class AuditPresentationProbeEndpoints
{
    public static void MapAuditPresentationProbeEndpoints(this WebApplication app) =>
        AuditExportEndpoints
            .Admin(app)
            .MapPost(
                "/{id:guid}/presentation-probe",
                async (Guid id, HttpRequest request, AuditRunService service, CancellationToken ct) =>
                {
                    using var doc = JsonDocument.Parse(await AuditCaptureProtocol.ReadAsync(request, 1024 * 1024, ct));
                    var root = doc.RootElement;
                    if (
                        root.ValueKind != JsonValueKind.Object
                        || root.EnumerateObject().Count() != 2
                        || !root.TryGetProperty("schemaVersion", out var version)
                        || version.ValueKind != JsonValueKind.Number
                        || !version.TryGetInt32(out var schema)
                        || schema != 1
                        || !root.TryGetProperty("probe", out var observation)
                    )
                    {
                        throw new AuditStoreException("invalid_audit_request", 400);
                    }
                    var probe = AuditCanonicalJson.Deserialize<FrozenPresentationProbe>(
                        Encoding.UTF8.GetBytes(observation.GetRawText())
                    );
                    var run = await service.PresentationProbeAsync(
                        id,
                        probe,
                        request.Headers["Idempotency-Key"].ToString(),
                        ct
                    );
                    return Results.Accepted($"/api/audits/runs/{run:D}", new { schemaVersion = 1, runId = run });
                }
            );
}
