using System.Text.Json;
using AiObservatory.Data.Audit;

namespace AiObservatory.Api.Audit;

public static class AuditRetentionEndpoints
{
    public static void MapAuditRetentionEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/api/audits")
            .RequireRateLimiting("api")
            .AddEndpointFilter<AuditProtocolFilter>()
            .AddEndpointFilter<AuditAdminAuthorityFilter>()
            .AddEndpointFilter<ApiKeyEndpointFilter>()
            .AddEndpointFilter<AdminOnlyApiKeyEndpointFilter>();
        admin.MapGet(
            "/retention",
            async (AuditStore store, CancellationToken ct) => Results.Ok(await store.GetRetentionAsync(ct))
        );
        admin.MapPatch(
            "/retention",
            async (HttpRequest request, AuditStore store, CancellationToken ct) =>
            {
                var bytes = await AuditCaptureProtocol.ReadAsync(request, 1024, ct);
                using var body = JsonDocument.Parse(bytes);
                var root = body.RootElement;
                if (
                    root.ValueKind != JsonValueKind.Object
                    || root.EnumerateObject().Count() != 2
                    || !root.TryGetProperty("evidenceDays", out var evidence)
                    || !root.TryGetProperty("summaryDays", out var summary)
                )
                {
                    throw new AuditStoreException("invalid_retention", 400);
                }
                await store.UpdateRetentionAsync(Days(evidence), Days(summary), ct);
                return Results.Ok(await store.GetRetentionAsync(ct));
            }
        );
        admin.MapGet(
            "/captures/{id:guid}/deletion-impact",
            async (Guid id, AuditRetentionService service, CancellationToken ct) =>
                Results.Ok(await service.GetDeletionImpactAsync(id, ct))
        );
        admin.MapDelete(
            "/captures/{id:guid}",
            async (Guid id, HttpRequest request, AuditRetentionService service, CancellationToken ct) =>
            {
                var digest = request.Headers.IfMatch.ToString();
                if (digest.Length == 66 && digest[0] == '"' && digest[^1] == '"')
                {
                    digest = digest[1..^1];
                }
                await service.DeleteCaptureAsync(id, digest, ct);
                return Results.NoContent();
            }
        );
        admin.MapDelete(
            "/runs/{id:guid}",
            async (Guid id, AuditRetentionService service, CancellationToken ct) =>
            {
                await service.DeleteRunAsync(id, ct);
                return Results.NoContent();
            }
        );
    }

    private static int? Days(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var days))
        {
            return days;
        }
        throw new AuditStoreException("invalid_retention", 400);
    }
}
