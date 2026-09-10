namespace AiObservatory.Api;

// Stricter sibling of ApiKeyEndpointFilter for GET routes whose data is more
// sensitive than the rest of the read surface (project/repo names reveal what
// Chris is working on, unlike aggregate spend). The readonly viewer key is
// never accepted here — only an Entra-authenticated user or the admin key.
public class AdminOnlyApiKeyEndpointFilter(IConfiguration config, IHostEnvironment env) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (context.HttpContext.User.Identity?.IsAuthenticated == true)
        {
            return await next(context);
        }

        var expectedAdmin = config["OBSERVATORY_API_KEY"];
        if (string.IsNullOrEmpty(expectedAdmin))
        {
            // Fail closed outside dev, mirroring ApiKeyEndpointFilter's behavior for
            // a missing key — a misconfigured deploy must not silently allow access.
            return env.IsDevelopment() ? await next(context) : Results.StatusCode(503);
        }

        if (
            !context.HttpContext.Request.Headers.TryGetValue("X-Observatory-Key", out var provided)
            || !ApiKeyComparer.FixedTimeEquals(provided.ToString(), expectedAdmin)
        )
        {
            return Results.Unauthorized();
        }

        return await next(context);
    }
}
