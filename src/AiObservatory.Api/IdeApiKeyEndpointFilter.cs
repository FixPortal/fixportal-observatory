namespace AiObservatory.Api;

public sealed class IdeApiKeyEndpointFilter(IConfiguration configuration, IHostEnvironment environment)
    : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var expected = configuration["OBSERVATORY_IDE_API_KEY"];
        if (string.IsNullOrEmpty(expected))
        {
            return environment.IsDevelopment() ? await next(context) : Results.StatusCode(503);
        }
        if (
            !context.HttpContext.Request.Headers.TryGetValue("X-Observatory-IDE-Key", out var provided)
            || !ApiKeyComparer.FixedTimeEquals(provided.ToString(), expected)
        )
        {
            return Results.Unauthorized();
        }
        return await next(context);
    }
}
