using System.Text.Json;
using AiObservatory.Api.Audit;
using AiObservatory.Data.Audit;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace AiObservatory.Api.Endpoints;

public static class OperationalInventoryEndpoints
{
    public const string Route = "/api/admin/operational-inventory";
    internal const int MaximumResponseBytes = 32 * 1024;

    public static void UseOperationalInventoryPrivacy(this WebApplication app) =>
        app.Use(
            async (context, next) =>
            {
                if (context.Request.Path.StartsWithSegments(Route, StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.Headers.CacheControl = "no-store";
                }
                await next(context);
            }
        );

    public static void MapOperationalInventoryEndpoint(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<IOptionsMonitor<OperationalInventoryOptions>>().CurrentValue.Enabled)
        {
            return;
        }
        app.MapGet(Route, ReadAsync)
            .RequireRateLimiting("api")
            .AddEndpointFilter(
                async (context, next) =>
                {
                    var http = context.HttpContext;
                    if (
                        !http
                            .RequestServices.GetRequiredService<IOptionsMonitor<OperationalInventoryOptions>>()
                            .CurrentValue.Enabled
                    )
                    {
                        return Results.NotFound();
                    }
                    return await next(context);
                }
            )
            .AddEndpointFilter<AuditAdminAuthorityFilter>()
            .AddEndpointFilter<OperationalInventoryAdminFilter>()
            .AddEndpointFilter(
                async (context, next) =>
                    context.HttpContext.Request.QueryString.HasValue
                    || context.HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true
                    || context.HttpContext.Request.ContentLength > 0
                    || context.HttpContext.Request.Headers.ContainsKey("Transfer-Encoding")
                        ? Results.BadRequest(new { code = "invalid_inventory_request" })
                        : await next(context)
            )
            .AddEndpointFilter<OperationalInventoryLimitFilter>();
    }

    private static async Task<IResult> ReadAsync(
        HttpContext http,
        IAuditOperationalInventoryReader reader,
        IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json,
        ILoggerFactory loggerFactory
    )
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var inventory = await reader.ReadAsync(deadline.Token);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(inventory, json.Value.SerializerOptions);
            if (bytes.Length > MaximumResponseBytes)
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
        catch (Exception ex)
        {
            LogFailureClass(loggerFactory, ex.GetType().Name);
            return Unavailable();
        }
    }

    private static IResult Unavailable() => Results.Json(new { code = "inventory_unavailable" }, statusCode: 503);

    private static void LogFailureClass(ILoggerFactory loggerFactory, string failureClass) =>
        loggerFactory
            .CreateLogger("OperationalInventory")
            .LogWarning("Operational inventory unavailable ({FailureClass})", failureClass);
}
