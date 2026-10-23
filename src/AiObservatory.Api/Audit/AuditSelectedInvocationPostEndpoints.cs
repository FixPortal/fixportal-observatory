using System.Text;
using System.Text.Json;
using AiObservatory.Data.Audit;
using Microsoft.Extensions.Options;
using NodaTime;

namespace AiObservatory.Api.Audit;

public static partial class AuditSelectedInvocationEndpoints
{
    public const string InvocationRoute = Prefix + "/invoke";
    private static readonly JsonSerializerOptions FixedPostJson = new(JsonSerializerDefaults.Web);

    private static void MapInvocation(WebApplication app, SelectedInvocationAuthority authority) =>
        app.MapPost(InvocationRoute, (Delegate)((HttpContext http) => InvokeAsync(http, authority)))
            .RequireRateLimiting("api")
            .AddEndpointFilter<AuditProtocolFilter>()
            .AddEndpointFilter<AuditAdminAuthorityFilter>()
            .AddEndpointFilter<OperationalInventoryAdminFilter>()
            .AddEndpointFilter<AuditSelectedInvocationLimitFilter>();

    private static async Task<IResult> InvokeAsync(HttpContext http, SelectedInvocationAuthority authority)
    {
        // One timer starts before any body read and is never reset after parsing.
        using var budget = new CancellationTokenSource(
            TimeSpan.FromMinutes(4),
            http.RequestServices.GetRequiredService<TimeProvider>()
        );
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(
            budget.Token,
            http.RequestAborted,
            http.RequestServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping
        );
        SelectedInvocationRequest request;
        try
        {
            var audit = http.RequestServices.GetRequiredService<IOptions<AuditOptions>>().Value;
            if (!authority.Enabled)
            {
                return PostResult(http, authority, new("unavailable", "selected_execution_disabled"));
            }
            if (audit.WorkerEnabled)
            {
                return PostResult(http, authority, new("conflict", "selected_execution_disabled"));
            }
            if (http.RequestServices.GetRequiredService<IClock>().GetCurrentInstant() >= authority.ExpiresAt)
            {
                return PostResult(http, authority, new("conflict", "selected_admission_expired"));
            }
            request = await AuditSelectedInvocationRequestReader.ReadAsync(http.Request, authority, attempt.Token);
            attempt.Token.ThrowIfCancellationRequested();
        }
        catch (AuditStoreException ex)
        {
            // This catch is structurally before execution-service resolution/core entry.
            return PostResult(
                http,
                authority,
                new(ex.StatusCode == 409 ? "conflict" : "rejected", ex.Code),
                ex.StatusCode
            );
        }
        catch (Exception)
        {
            return AmbiguousPost(authority);
        }
        try
        {
            var service = http.RequestServices.GetRequiredService<IAuditSelectedInvocationService>();
            var outcome = await service.InvokeAsync(authority, request, attempt.Token);
            attempt.Token.ThrowIfCancellationRequested();
            return PostResult(http, authority, outcome);
        }
        catch (Exception)
        {
            // Contain all post-call exceptions before AuditProtocolFilter sees a nominal code.
            return AmbiguousPost(authority);
        }
    }

    private static IResult PostResult(
        HttpContext http,
        SelectedInvocationAuthority authority,
        SelectedInvocationOutcome outcome,
        int? statusCode = null
    )
    {
        var status =
            statusCode
            ?? outcome.Status switch
            {
                "completed" or "admitted" => 200,
                "pending" => 202,
                "busy" or "conflict" => 409,
                _ => 503,
            };
        var options = http
            .RequestServices.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
            .Value.SerializerOptions;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(PostResponse(authority, outcome), options);
        return bytes.Length <= 8192
            ? Results.Content(Encoding.UTF8.GetString(bytes), "application/json", statusCode: status)
            : AmbiguousPost(authority);
    }

    private static SelectedInvocationPostResponse PostResponse(
        SelectedInvocationAuthority authority,
        SelectedInvocationOutcome outcome
    ) =>
        new(
            1,
            outcome.Status,
            outcome.Code,
            authority.Binding.RunId,
            authority.InvocationId,
            authority.Binding.ScopeDigest,
            authority.Binding.ExpectedRevision,
            outcome.Current
        );

    private static IResult AmbiguousPost(SelectedInvocationAuthority authority) =>
        Results.Content(
            JsonSerializer.Serialize(PostResponse(authority, new("ambiguous", "selected_ambiguous")), FixedPostJson),
            "application/json",
            statusCode: 503
        );
}
