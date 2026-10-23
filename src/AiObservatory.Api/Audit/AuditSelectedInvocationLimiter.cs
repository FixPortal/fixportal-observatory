using NodaTime;

namespace AiObservatory.Api.Audit;

/// <summary>One shared cooldown per verified authority, independent of operation and inventory.</summary>
public sealed class AuditSelectedInvocationLimiter(IClock clock)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Instant> _admitted = new(StringComparer.Ordinal);

    public bool TryAcquire(string verifiedAuthority)
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
            if (_admitted.ContainsKey(verifiedAuthority) || _admitted.Count >= 128)
            {
                return false;
            }
            _admitted.Add(verifiedAuthority, now);
            return true;
        }
    }
}

public sealed class AuditSelectedInvocationLimitFilter(AuditSelectedInvocationLimiter limiter) : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        context.HttpContext.Items[OperationalInventoryAdminFilter.AuthorityItem] is string authority
        && limiter.TryAcquire(authority)
            ? next(context)
            : ValueTask.FromResult<object?>(Results.StatusCode(StatusCodes.Status429TooManyRequests));
}
