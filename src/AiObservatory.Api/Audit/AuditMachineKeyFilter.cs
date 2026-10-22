using System.Text.Json;
using AiObservatory.Audit.Contracts;
using AiObservatory.Data.Audit;
using Microsoft.Extensions.Options;

namespace AiObservatory.Api.Audit;

public sealed class AuditProtocolFilter(IOptions<AuditOptions> options) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!options.Value.Enabled)
        {
            return Results.NotFound();
        }
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        try
        {
            return await next(context);
        }
        catch (AuditStoreException ex)
        {
            return Results.Json(new { code = ex.Code }, statusCode: ex.StatusCode);
        }
        catch (AuditValidationException)
        {
            return Results.BadRequest(new { code = "invalid_audit_request" });
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { code = "invalid_audit_request" });
        }
    }
}

public sealed class AuditMachineKeyFilter(AuditCredentialService credentials) : IEndpointFilter
{
    internal static readonly object CredentialItem = new();

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.HttpContext.Request;
        var credential = await credentials.VerifyAsync(
            request.Headers["X-Observatory-Audit-Key"].ToString(),
            context.HttpContext.RequestAborted
        );
        if (credential == null)
        {
            return Results.Unauthorized();
        }
        context.HttpContext.Items[CredentialItem] = credential;
        return await next(context);
    }

    internal static AuditCredentialView Credential(HttpContext context) =>
        (AuditCredentialView)context.Items[CredentialItem]!;
}

/// <summary>A viewer credential must remain read-only even when a human session is also present.</summary>
public sealed class AuditAdminAuthorityFilter(IConfiguration configuration) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var viewerKey = configuration["OBSERVATORY_READONLY_API_KEY"];
        var request = context.HttpContext.Request;
        if (
            context.HttpContext.User.Identity?.IsAuthenticated == true
            && !string.IsNullOrEmpty(viewerKey)
            && request
                .Headers["X-Observatory-Key"]
                .Any(value => value != null && ApiKeyComparer.FixedTimeEquals(value, viewerKey))
        )
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
        return await next(context);
    }
}
