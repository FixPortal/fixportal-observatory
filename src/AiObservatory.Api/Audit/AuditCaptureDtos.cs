using System.Text.Json;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using AiObservatory.Data.Audit;

namespace AiObservatory.Api.Audit;

public sealed record IssuedAuditCredential(Guid MachineId, Guid CredentialId, string Secret);

public sealed record MachineLease(Guid MachineId, Guid CredentialId, Guid Token);

public sealed record SealCaptureRequest(
    int SchemaVersion,
    CaptureManifest Manifest,
    string Sha256,
    string IdempotencyKey
);

public sealed record SealCaptureResult(Guid CaptureId);

internal static class AuditCaptureProtocol
{
    internal static async Task<byte[]> ReadAsync(HttpRequest request, int limit, CancellationToken ct)
    {
        if (request.ContentLength > limit)
        {
            throw new AuditStoreException("audit_body_limit", 413);
        }
        using var output = new MemoryStream();
        var buffer = new byte[65536];
        int read;
        while ((read = await request.Body.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + read > limit)
            {
                throw new AuditStoreException("audit_body_limit", 413);
            }
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return output.ToArray();
    }

    internal static async Task<JsonDocument> Envelope(HttpRequest request, bool seal, CancellationToken ct)
    {
        var document = JsonDocument.Parse(await ReadAsync(request, seal ? 1024 * 1024 + 4096 : 1024, ct));
        try
        {
            var root = document.RootElement;
            var names = seal ? new[] { "schemaVersion", "manifest", "sha256" } : ["schemaVersion"];
            if (
                root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Count() != names.Length
                || names.Any(name => root.EnumerateObject().Count(p => p.NameEquals(name)) != 1)
                || root.GetProperty("schemaVersion").ValueKind != JsonValueKind.Number
                || !root.GetProperty("schemaVersion").TryGetInt32(out var version)
                || version != 1
            )
            {
                throw new AuditStoreException("invalid_audit_request", 400);
            }
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    internal static async Task<SealCaptureRequest> SealBody(HttpRequest request, CancellationToken ct)
    {
        using var document = await Envelope(request, true, ct);
        var root = document.RootElement;
        var original = System.Text.Encoding.UTF8.GetBytes(root.GetProperty("manifest").GetRawText());
        var manifest = AuditCanonicalJson.Deserialize<CaptureManifest>(original);
        if (!original.AsSpan().SequenceEqual(AuditCanonicalJson.Serialize(manifest)))
        {
            throw new AuditStoreException("noncanonical_manifest", 400);
        }
        if (root.GetProperty("sha256").ValueKind != JsonValueKind.String)
        {
            throw new AuditStoreException("invalid_audit_request", 400);
        }
        return new(
            1,
            manifest,
            root.GetProperty("sha256").GetString() ?? "",
            request.Headers["Idempotency-Key"].ToString()
        );
    }

    internal static Guid LeaseToken(HttpRequest request) =>
        Guid.TryParseExact(request.Headers["X-Observatory-Audit-Lease"].ToString(), "D", out var token)
        && token != Guid.Empty
            ? token
            : throw new AuditStoreException("invalid_audit_lease", 400);
}
