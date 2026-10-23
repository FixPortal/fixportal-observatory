using System.Text.Json;
using AiObservatory.Data.Audit;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace AiObservatory.Api.Audit;

public static partial class AuditSelectedInvocationEndpoints
{
    public const string Prefix = "/api/admin/audit-selected-run";
    public const string BindingRoute = Prefix + "/binding";
    public static void UseAuditSelectedInvocationPrivacy(this WebApplication app) => app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Headers.CacheControl = "no-store";
        }
        await next(context);
    });

    public static void MapAuditSelectedInvocationEndpoints(this WebApplication app)
    {
        // Capture configured authority once. Reconciliation never refreshes it from a row or reload.
        var authority = app.Services.GetRequiredService<IOptions<AuditSelectedInvocationOptions>>().Value.ToAuthority();
        if (authority == null || !app.Services.GetRequiredService<IOptions<AuditOptions>>().Value.Enabled)
        {
            return;
        }
        app.MapGet(BindingRoute, (Delegate)((HttpContext http) => ReadAsync(http, authority)))
            .RequireRateLimiting("api")
            .AddEndpointFilter<AuditProtocolFilter>()
            .AddEndpointFilter<AuditAdminAuthorityFilter>()
            .AddEndpointFilter<OperationalInventoryAdminFilter>()
            .AddEndpointFilter(async (context, next) =>
                context.HttpContext.Request.QueryString.HasValue
                || context.HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true
                || context.HttpContext.Request.ContentLength > 0
                || context.HttpContext.Request.Headers.ContainsKey("Transfer-Encoding")
                    ? Results.BadRequest(new { code = "selected_request_invalid" })
                    : await next(context))
            .AddEndpointFilter<AuditSelectedInvocationLimitFilter>();
        MapInvocation(app, authority);
    }

    private static async Task<IResult> ReadAsync(HttpContext http, SelectedInvocationAuthority authority)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var metadata = await http.RequestServices.GetRequiredService<IAuditSelectedInvocationMetadataReader>()
                .ReadAsync(authority.Binding, deadline.Token);
            if (metadata == null)
            {
                return Results.NotFound(new { code = "selected_binding_not_found" });
            }
            if (metadata.RunId != authority.Binding.RunId || metadata.FullScopeDigest != authority.Binding.ScopeDigest)
            {
                return Unavailable();
            }
            var options = http.RequestServices.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(Response(metadata, authority), options.Value.SerializerOptions);
            if (bytes.Length > 32 * 1024)
            {
                return Unavailable();
            }
            deadline.Token.ThrowIfCancellationRequested();
            return Results.Bytes(bytes, "application/json");
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (AuditStoreException ex) when (ex.Code == "selected_binding_conflict")
        {
            return Results.Conflict(new { code = "selected_binding_conflict" });
        }
        catch (Exception)
        {
            LogUnavailable(http);
            return Unavailable();
        }
    }

    private static IResult Unavailable() => Results.Json(new { code = "selected_metadata_unavailable" }, statusCode: 503);

    private static void LogUnavailable(HttpContext http) =>
        http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("AuditSelectedInvocation")
            .LogWarning("Selected invocation metadata unavailable");

    private static SelectedInvocationBindingResponse Response(SelectedRunMetadata metadata, SelectedInvocationAuthority authority) =>
        new(1, authority.Binding.RunId, authority.InvocationId, authority.Binding.ScopeDigest, authority.Binding.ExpectedRevision,
            metadata.CurrentRevision, metadata.State, metadata.Stage, metadata.Attempts, metadata.CaptureRequestIds,
            metadata.Requests.Select(r => new SelectedInvocationBindingRequest(r.RequestId, r.MachineId, r.RequestScopeDigest,
                r.Deadline.ToDateTimeOffset(), r.State)).ToArray(), metadata.CaptureIds,
            metadata.EvidenceExpiresAt?.ToDateTimeOffset(), metadata.SummaryExpiresAt?.ToDateTimeOffset(), metadata.DiagnosticCode, metadata.ResultAvailable);
}
