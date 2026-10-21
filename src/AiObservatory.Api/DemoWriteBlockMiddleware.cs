namespace AiObservatory.Api;

/// <summary>
/// Demo mode is read-only for everyone, including the admin key. A middleware rather than an
/// endpoint filter on purpose: the IDE route group (<c>/api/ide/v1</c>) has its own filter and
/// accepts POSTs, and a filter on the <c>/api</c> group would not see it. Seed and reset stay
/// reachable; both are still admin-key-gated by <see cref="ApiKeyEndpointFilter"/>.
/// </summary>
public sealed class DemoWriteBlockMiddleware(RequestDelegate next)
{
    private static readonly string[] ExemptPaths = ["/api/dev/seed", "/api/dev/reset-demo"];

    public Task InvokeAsync(HttpContext context)
    {
        var method = context.Request.Method;
        var safe = HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);
        // Routing treats a trailing slash as the same endpoint, so the exempt match must too.
        var path = (context.Request.Path.Value ?? string.Empty).TrimEnd('/');
        if (safe || ExemptPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            return next(context);
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return context.Response.WriteAsync("This is a read-only demo instance.", context.RequestAborted);
    }
}
