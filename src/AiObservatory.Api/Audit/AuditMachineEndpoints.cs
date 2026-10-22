using AiObservatory.Data.Audit;

namespace AiObservatory.Api.Audit;

public static class AuditMachineEndpoints
{
    public static void MapAuditMachineEndpoints(this WebApplication app)
    {
        var machines = app.MapGroup("/api/audits/machines")
            .RequireRateLimiting("api")
            .AddEndpointFilter<AuditProtocolFilter>()
            .AddEndpointFilter<AuditAdminAuthorityFilter>()
            .AddEndpointFilter<ApiKeyEndpointFilter>()
            .AddEndpointFilter<AdminOnlyApiKeyEndpointFilter>();
        machines.MapPost(
            "",
            async (HttpRequest request, AuditCredentialService service, CancellationToken ct) =>
            {
                using var body = await AuditCaptureProtocol.Envelope(request, false, ct);
                return Results.Ok(await service.IssueAsync(Guid.Empty, ct));
            }
        );
        machines.MapPost(
            "/{id:guid}/rotate",
            async (Guid id, HttpRequest request, AuditCredentialService service, CancellationToken ct) =>
            {
                using var body = await AuditCaptureProtocol.Envelope(request, false, ct);
                if (id == Guid.Empty)
                {
                    throw new AuditStoreException("machine_not_found", 404);
                }
                return Results.Ok(await service.IssueAsync(id, ct));
            }
        );
        machines.MapDelete(
            "/{id:guid}/credential",
            async (Guid id, AuditStore store, CancellationToken ct) =>
            {
                await store.RevokeMachineCredentialAsync(id, ct);
                return Results.NoContent();
            }
        );
    }
}
