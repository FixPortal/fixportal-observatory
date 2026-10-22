using AiObservatory.Data.Audit;
using Microsoft.Extensions.Options;
using NodaTime;

namespace AiObservatory.Api.Audit;

public static class AuditCaptureEndpoints
{
    public static void MapAuditCaptureEndpoints(this WebApplication app)
    {
        var machine = app.MapGroup("/api/audits/capture-requests")
            .RequireRateLimiting("api")
            .AddEndpointFilter<AuditProtocolFilter>()
            .AddEndpointFilter<AuditMachineKeyFilter>();
        machine.MapPost(
            "/claim",
            async (HttpContext context, AuditStore store, IClock clock, CancellationToken ct) =>
            {
                using var body = await AuditCaptureProtocol.Envelope(context.Request, false, ct);
                var auth = AuditMachineKeyFilter.Credential(context);
                var lease = await store.TryClaimCaptureAsync(
                    auth.MachineId,
                    auth.CredentialId,
                    clock.GetCurrentInstant(),
                    ct
                );
                return lease == null
                    ? Results.NoContent()
                    : Results.Ok(
                        new
                        {
                            schemaVersion = 1,
                            lease.RequestId,
                            lease.MachineId,
                            leaseToken = lease.Token,
                            expiresAt = lease.ExpiresAt.ToDateTimeOffset(),
                            lease.Scope,
                        }
                    );
            }
        );
        machine.MapPut(
            "/{id:guid}/chunks/{index:int}",
            async (
                Guid id,
                int index,
                HttpContext context,
                AuditStore store,
                IOptions<AuditOptions> options,
                CancellationToken ct
            ) =>
            {
                var auth = AuditMachineKeyFilter.Credential(context);
                var token = AuditCaptureProtocol.LeaseToken(context.Request);
                var bytes = await AuditCaptureProtocol.ReadAsync(context.Request, options.Value.MaxChunkBytes, ct);
                var lease = await store.ResolveCaptureLeaseAsync(id, auth.MachineId, auth.CredentialId, token, ct);
                await store.PutChunkAsync(
                    lease,
                    index,
                    bytes,
                    context.Request.Headers["X-Content-SHA256"].ToString(),
                    ct
                );
                return Results.NoContent();
            }
        );
        machine.MapPost(
            "/{id:guid}/seal",
            async (Guid id, HttpContext context, AuditCaptureService service, CancellationToken ct) =>
            {
                var auth = AuditMachineKeyFilter.Credential(context);
                var lease = new MachineLease(
                    auth.MachineId,
                    auth.CredentialId,
                    AuditCaptureProtocol.LeaseToken(context.Request)
                );
                var body = await AuditCaptureProtocol.SealBody(context.Request, ct);
                return Results.Ok(await service.SealAsync(id, body, lease, ct));
            }
        );
    }
}
