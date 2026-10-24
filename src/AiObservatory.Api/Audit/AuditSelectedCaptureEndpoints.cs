using System.Text.Json;
using AiObservatory.Data.Audit;
using Microsoft.Extensions.Options;

namespace AiObservatory.Api.Audit;

public static class AuditSelectedCaptureEndpoints
{
    public const string Prefix = "/api/audits/capture-requests/selected";

    public static void MapAuditSelectedCaptureEndpoints(this WebApplication app)
    {
        var authority = app.Services.GetRequiredService<IOptions<AuditSelectedInvocationOptions>>().Value.ToAuthority();
        if (authority == null)
        {
            return;
        }
        var machine = app.MapGroup(Prefix)
            .RequireRateLimiting("api")
            .AddEndpointFilter<AuditProtocolFilter>()
            .AddEndpointFilter<AuditMachineKeyFilter>();
        machine.MapPost(
            "/claim",
            (HttpContext http, AuditStore store, IOptions<AuditOptions> options, CancellationToken ct) =>
                Capture(http, store, options.Value, authority, false, ct)
        );
        machine.MapPost(
            "/renew",
            (HttpContext http, AuditStore store, IOptions<AuditOptions> options, CancellationToken ct) =>
                Capture(http, store, options.Value, authority, true, ct)
        );
    }

    private static async Task<IResult> Capture(
        HttpContext http,
        AuditStore store,
        AuditOptions options,
        SelectedInvocationAuthority authority,
        bool renew,
        CancellationToken ct
    )
    {
        if (options.WorkerEnabled)
        {
            throw new AuditStoreException("selected_binding_conflict", 409);
        }
        var request = await SelectedCaptureRequest.ReadAsync(http.Request, ct);
        var auth = AuditMachineKeyFilter.Credential(http);
        if (
            request.RunId != authority.Binding.RunId
            || request.ExpectedRevision != authority.Binding.ExpectedRevision
            || request.FullScopeDigest != authority.Binding.ScopeDigest
            || request.MachineId != auth.MachineId
        )
        {
            throw new AuditStoreException("selected_binding_conflict", 409);
        }
        // Enabled controls selected execution, not this separately machine-authorized capture.
        // Complete immutable binding and its expiry remain mandatory for both operations.
        var lease = renew
            ? await store.RenewSelectedCaptureAsync(
                authority.Binding,
                request.RequestId,
                auth.MachineId,
                auth.CredentialId,
                AuditCaptureProtocol.LeaseToken(http.Request),
                request.RequestScopeDigest,
                authority.ExpiresAt,
                ct
            )
            : await store.ClaimSelectedCaptureAsync(
                authority.Binding,
                request.RequestId,
                auth.MachineId,
                auth.CredentialId,
                request.RequestScopeDigest,
                authority.ExpiresAt,
                ct
            );
        return Results.Ok(
            new
            {
                schemaVersion = 1,
                lease.InstanceId,
                lease.RunId,
                lease.ExpectedRevision,
                lease.FullScopeDigest,
                lease.RequestId,
                lease.MachineId,
                leaseToken = lease.Token,
                lease.RequestScopeDigest,
                lease.Scope,
                expiresAt = lease.ExpiresAt.ToDateTimeOffset(),
                serverObservedAt = lease.ServerObservedAt.ToDateTimeOffset(),
            }
        );
    }
}

internal sealed record SelectedCaptureRequest(
    Guid RunId,
    long ExpectedRevision,
    string FullScopeDigest,
    Guid RequestId,
    Guid MachineId,
    string RequestScopeDigest
)
{
    internal static async Task<SelectedCaptureRequest> ReadAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.QueryString.HasValue)
        {
            throw Invalid();
        }
        using var json = JsonDocument.Parse(await AuditCaptureProtocol.ReadAsync(request, 1024, ct));
        var root = json.RootElement;
        string[] names =
        [
            "schemaVersion",
            "runId",
            "expectedRevision",
            "fullScopeDigest",
            "requestId",
            "machineId",
            "requestScopeDigest",
        ];
        if (
            root.ValueKind != JsonValueKind.Object
            || root.EnumerateObject().Count() != names.Length
            || names.Any(n => root.EnumerateObject().Count(p => p.NameEquals(n)) != 1)
            || root.GetProperty("schemaVersion").ValueKind != JsonValueKind.Number
            || !root.GetProperty("schemaVersion").TryGetInt32(out var version)
            || version != 1
            || root.GetProperty("expectedRevision").ValueKind != JsonValueKind.Number
            || !root.GetProperty("expectedRevision").TryGetInt64(out var revision)
            || revision < 0
        )
        {
            throw Invalid();
        }
        return new(
            Id(root, "runId"),
            revision,
            Digest(root, "fullScopeDigest"),
            Id(root, "requestId"),
            Id(root, "machineId"),
            Digest(root, "requestScopeDigest")
        );
    }

    private static Guid Id(JsonElement root, string name) =>
        root.GetProperty(name).ValueKind == JsonValueKind.String
        && Guid.TryParseExact(root.GetProperty(name).GetString(), "D", out var id)
        && id != Guid.Empty
        && root.GetProperty(name).GetString() == id.ToString("D")
            ? id
            : throw Invalid();

    private static string Digest(JsonElement root, string name) =>
        root.GetProperty(name).ValueKind == JsonValueKind.String
        && root.GetProperty(name).GetString() is { Length: 64 } value
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? value
            : throw Invalid();

    private static AuditStoreException Invalid() => new("invalid_audit_request", 400);
}
