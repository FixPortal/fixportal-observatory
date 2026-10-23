using System.Globalization;
using System.Text;
using System.Text.Json;
using AiObservatory.Data.Audit;
using Microsoft.Net.Http.Headers;

namespace AiObservatory.Api.Audit;

public static class AuditSelectedInvocationRequestReader
{
    public static async Task<SelectedInvocationRequest> ReadAsync(HttpRequest request, SelectedInvocationAuthority authority, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        RequireHeaders(request, authority);
        var bytes = await ReadBody(request, ct);
        try
        {
            new UTF8Encoding(false, true).GetCharCount(bytes);
            using var document = JsonDocument.Parse(bytes);
            return Parse(document.RootElement, authority);
        }
        catch (JsonException)
        {
            throw InvalidRequest();
        }
        catch (DecoderFallbackException)
        {
            throw InvalidRequest();
        }
    }

    private static void RequireHeaders(HttpRequest request, SelectedInvocationAuthority authority)
    {
        if (request.QueryString.HasValue || request.Headers.ContainsKey("If-Match"))
        {
            throw InvalidRequest();
        }
        var correlation = request.Headers["Idempotency-Key"];
        if (correlation.Count != 1 || correlation[0] != authority.InvocationId.ToString("D"))
        {
            throw InvalidRequest();
        }
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType)
            || !string.Equals(contentType.MediaType.Value, "application/json", StringComparison.OrdinalIgnoreCase)
            || contentType.Charset.HasValue && !string.Equals(contentType.Charset.Value.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase))
        {
            throw new AuditStoreException("selected_content_type", 415);
        }
    }

    private static async Task<byte[]> ReadBody(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength > 1024)
        {
            throw new AuditStoreException("selected_body_limit", 413);
        }
        var buffer = new byte[1025];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await request.Body.ReadAsync(buffer.AsMemory(count), ct);
            if (read == 0)
            {
                return buffer[..count];
            }
            count += read;
        }
        throw new AuditStoreException("selected_body_limit", 413);
    }

    private static SelectedInvocationRequest Parse(JsonElement root, SelectedInvocationAuthority authority)
    {
        RequireShape(root);
        if (!CanonicalInteger(root.GetProperty("schemaVersion"), out var schema) || schema != 1
            || !CanonicalInteger(root.GetProperty("expectedRevision"), out var revision))
        {
            throw InvalidRequest();
        }
        var digestValue = root.GetProperty("fullScopeDigest");
        if (digestValue.ValueKind != JsonValueKind.String)
        {
            throw InvalidRequest();
        }
        var digest = digestValue.GetString();
        if (digest is not { Length: 64 } || !digest.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
            || digestValue.GetRawText() != "\"" + digest + "\"")
        {
            throw InvalidRequest();
        }
        if (revision != authority.Binding.ExpectedRevision || digest != authority.Binding.ScopeDigest)
        {
            throw new AuditStoreException("selected_binding_conflict", 409);
        }
        return new(1, revision, digest);
    }

    private static void RequireShape(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw InvalidRequest();
        }
        var properties = root.EnumerateObject().ToArray();
        if (properties.Length != 3 || properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != 3
            || properties.Any(p => p.Name is not ("schemaVersion" or "expectedRevision" or "fullScopeDigest")))
        {
            throw InvalidRequest();
        }
    }

    private static bool CanonicalInteger(JsonElement value, out long number)
    {
        number = default;
        if (value.ValueKind != JsonValueKind.Number)
        {
            return false;
        }
        var text = value.GetRawText();
        return (text == "0" || text.Length > 0 && text[0] is >= '1' and <= '9')
            && text.All(char.IsAsciiDigit)
            && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    private static AuditStoreException InvalidRequest() => new("selected_request_invalid", 400);
}
