using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using NodaTime;

namespace AiObservatory.Api;

/// <summary>Strict authority for operational metadata, including Development.</summary>
public sealed class OperationalInventoryAdminFilter(IConfiguration configuration) : IEndpointFilter
{
    internal static readonly object AuthorityItem = new();

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var supplied = http.Request.Headers["X-Observatory-Key"];
        var viewer = configuration["OBSERVATORY_READONLY_API_KEY"];
        if (
            !string.IsNullOrEmpty(viewer)
            && supplied.Any(key => key != null && ApiKeyComparer.FixedTimeEquals(key, viewer))
        )
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
        if (supplied.Count > 1 || supplied.Count == 1 && supplied[0]!.Contains(','))
        {
            return Results.Unauthorized();
        }
        if (http.User.Identity?.IsAuthenticated == true)
        {
            var identity =
                http.User.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier")
                ?? http.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? http.User.FindFirstValue("sub");
            var tenant = http.User.FindFirstValue("http://schemas.microsoft.com/identity/claims/tenantid") ?? "";
            http.Items[AuthorityItem] =
                identity == null
                    ? "authenticated"
                    : "user:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tenant + "\n" + identity)));
            return await next(context);
        }
        var admin = configuration["OBSERVATORY_API_KEY"];
        if (string.IsNullOrEmpty(admin))
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        if (supplied.Count != 1 || !ApiKeyComparer.FixedTimeEquals(supplied[0]!, admin))
        {
            return Results.Unauthorized();
        }
        http.Items[AuthorityItem] = "admin";
        return await next(context);
    }
}

/// <summary>A bounded, queue-free one-minute cooldown after verified authority.</summary>
public sealed class OperationalInventoryLimiter(IClock clock)
{
    internal const int MaximumPartitions = 128;
    private readonly object _gate = new();
    private readonly Dictionary<string, Instant> _admitted = new(StringComparer.Ordinal);

    public bool TryAcquire(string authority)
    {
        lock (_gate)
        {
            var now = clock.GetCurrentInstant();
            foreach (
                var expired in _admitted
                    .Where(pair => pair.Value + Duration.FromMinutes(1) <= now)
                    .Select(pair => pair.Key)
                    .ToArray()
            )
            {
                _admitted.Remove(expired);
            }
            if (_admitted.ContainsKey(authority) || _admitted.Count >= MaximumPartitions)
            {
                return false;
            }
            _admitted.Add(authority, now);
            return true;
        }
    }
}

public sealed class OperationalInventoryLimitFilter(OperationalInventoryLimiter limiter) : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        context.HttpContext.Items[OperationalInventoryAdminFilter.AuthorityItem] is string authority
        && limiter.TryAcquire(authority)
            ? next(context)
            : ValueTask.FromResult<object?>(Results.StatusCode(StatusCodes.Status429TooManyRequests));
}
