namespace AiObservatory.Api;

public class ApiKeyEndpointFilter(IConfiguration config, IHostEnvironment env) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        // A human signed in via Entra (JWT bearer) gets full access — read and write.
        // Machine callers (the observe-stop / sweeper / gemini-review hooks) carry no
        // token and fall through to the API-key checks below.
        if (context.HttpContext.User.Identity?.IsAuthenticated == true)
        {
            return await next(context);
        }

        var expectedAdmin = config["OBSERVATORY_API_KEY"];
        var expectedReadonly = config["OBSERVATORY_READONLY_API_KEY"];

        return HttpMethods.IsGet(context.HttpContext.Request.Method)
            ? await AuthorizeGetAsync(context, next, expectedAdmin, expectedReadonly)
            : await AuthorizeAdminAsync(context, next, expectedAdmin);
    }

    private async ValueTask<object?> AuthorizeGetAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next,
        string? expectedAdmin,
        string? expectedReadonly
    )
    {
        if (string.IsNullOrEmpty(expectedReadonly))
        {
            // A configured admin key still works on GET even when the readonly key
            // hasn't been set — otherwise admin-only routes become unreachable.
            if (!string.IsNullOrEmpty(expectedAdmin))
            {
                if (HasMatchingKey(context, expectedAdmin))
                {
                    return await next(context);
                }

                return Results.Unauthorized();
            }

            // Fail closed: a missing read-only key must not silently open raw telemetry.
            return await ContinueInDevelopmentAsync(context, next);
        }

        if (!context.HttpContext.Request.Headers.TryGetValue("X-Observatory-Key", out var provided))
        {
            return Results.Unauthorized();
        }

        var providedKey = provided.ToString();
        var matchesReadonly = ApiKeyComparer.FixedTimeEquals(providedKey, expectedReadonly);
        var matchesAdmin =
            !string.IsNullOrEmpty(expectedAdmin) && ApiKeyComparer.FixedTimeEquals(providedKey, expectedAdmin);

        if (!matchesReadonly && !matchesAdmin)
        {
            return Results.Unauthorized();
        }

        return await next(context);
    }

    private async ValueTask<object?> AuthorizeAdminAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next,
        string? expectedAdmin
    )
    {
        if (string.IsNullOrEmpty(expectedAdmin))
        {
            return await ContinueInDevelopmentAsync(context, next);
        }

        if (!HasMatchingKey(context, expectedAdmin))
        {
            return Results.Unauthorized();
        }

        return await next(context);
    }

    private async ValueTask<object?> ContinueInDevelopmentAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next
    ) => env.IsDevelopment() ? await next(context) : Results.StatusCode(503);

    private static bool HasMatchingKey(EndpointFilterInvocationContext context, string expected) =>
        context.HttpContext.Request.Headers.TryGetValue("X-Observatory-Key", out var provided)
        && ApiKeyComparer.FixedTimeEquals(provided.ToString(), expected);
}
