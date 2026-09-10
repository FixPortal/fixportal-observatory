using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiObservatory.Data.Entities;
using NodaTime;

namespace AiObservatory.Data.Pricing;

public sealed record PricingSnapshotCandidate(
    Provider Provider,
    string SourceId,
    Instant RetrievedAt,
    string SourceUrl,
    string ContentHash,
    string RawEvidence,
    string NormalizedCatalog
)
{
    /// <summary>
    /// Snapshot identity: the SHA-256 of the raw evidence AND the normalized catalog content,
    /// excluding fetch-derived stamps. Including the normalized content means a normaliser fix
    /// that produces a corrected catalog from unchanged provider evidence still counts as new
    /// content and is activated (and repriced from) instead of short-circuiting as
    /// <see cref="PricingActivationResult.Unchanged"/>. The excluded stamps — the catalog's
    /// <c>retrievedAt</c> and every entry's assumed (non-provider-declared) <c>effectiveFrom</c>,
    /// which live sources set to the fetch date — change on every re-fetch of unchanged
    /// evidence, so hashing them would activate a byte-identical duplicate snapshot (and run a
    /// redundant full reprice) after every refresh. Provider-declared effective dates are real
    /// content and stay hashed.
    /// </summary>
    public static string ComputeContentHash(string rawEvidence, string normalizedCatalog)
    {
        var identity = rawEvidence + '\n' + WithoutFetchStamps(normalizedCatalog);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private static string WithoutFetchStamps(string normalizedCatalog)
    {
        try
        {
            var catalog = JsonNode.Parse(normalizedCatalog);
            // AsObject() would throw InvalidOperationException (NOT JsonException, so it would
            // escape the catch below) for array/scalar roots; pattern-match instead so every
            // non-object root takes the same unmodified-string fallback as malformed JSON.
            if (catalog is not JsonObject root)
            {
                return normalizedCatalog;
            }

            root.Remove("retrievedAt");
            if (root["entries"] is JsonArray entries)
            {
                var assumedDates = entries
                    .OfType<JsonObject>()
                    .Where(entry =>
                        entry["effectiveDateIsProviderDeclared"] is JsonValue declared
                        && declared.TryGetValue<bool>(out var isProviderDeclared)
                        && !isProviderDeclared
                    );
                foreach (var entry in assumedDates)
                {
                    entry.Remove("effectiveFrom");
                }
            }

            return root.ToJsonString();
        }
        catch (JsonException)
        {
            // Validation reports malformed catalogs with a proper error; identity just falls
            // back to the unmodified string so that error path stays intact.
            return normalizedCatalog;
        }
    }
}

public static class PricingSourceIds
{
    public const string OpenAi = "openai-pricing";
    public const string Claude = "claude-pricing";
    public const string Kimi = "kimi-pricing";
    public const string GoogleCloudCatalog = "google-cloud-catalog";
    public const string GeminiDeveloperApi = "gemini-developer-api-pricing";
}

public enum PricingActivationResult
{
    Activated,
    Unchanged,
}
