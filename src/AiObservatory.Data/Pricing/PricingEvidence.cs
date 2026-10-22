using System.Globalization;
using System.Text.Json;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using AiObservatory.Data.Entities;
using NodaTime;

namespace AiObservatory.Data.Pricing;

/// <summary>Allowlisted typed observations, independent of any historical catalog association.</summary>
public static class PricingEvidence
{
    public static PricingDimensions Dimensions(UsageEvent usage)
    {
        using var json = ProviderPricingJson.Evidence(usage.RawPayload);
        var root = json.RootElement;
        string? Text(string name) =>
            ProviderPricingJson.TryString(root, name, out var value) && Safe(value)
                ? value.ToLowerInvariant().Replace(' ', '_')
                : null;
        var tier = Text("service_tier") ?? Text("processing") ?? Text("tier");
        return new(
            Enum.TryParse<ServiceTier>(tier, true, out var parsed) ? parsed : ServiceTier.Unknown,
            tier == "batch" || ProviderPricingJson.TryBoolean(root, "batch", out var batch) && batch,
            Text("region"),
            ProviderPricingJson.TryInt64(root, "context_tokens", out var context) && context >= 0 ? context : null
        )
        {
            SourceServiceTier = Text("service_tier"),
            Speed = Text("speed"),
            InferenceGeo = Text("inference_geo"),
            Processing = Text("processing"),
            Context = Text("context"),
            Service = Text("service"),
            Sku = Text("sku_id"),
            Modality = Text("modality"),
            CacheLane = Text("cache_lane"),
            HighSpeed = ProviderPricingJson.TryBoolean(root, "high_speed", out var highSpeed) ? highSpeed : null,
            LongContext = ProviderPricingJson.TryBoolean(root, "long_context", out var longContext)
                ? longContext
                : null,
            ContextThreshold =
                ProviderPricingJson.TryInt64(root, "context_threshold", out var threshold) && threshold >= 0
                    ? threshold
                    : null,
        };
    }

    public static PricingDimensions SourceDimensions(UsageEvent usage)
    {
        using var json = ProviderPricingJson.Evidence(usage.RawPayload);
        var root = json.RootElement;
        string? Text(string name) =>
            ProviderPricingJson.TryString(root, name, out var value) && Safe(value) ? value : null;
        var tier = Text("service_tier");
        return new(
            Enum.TryParse<ServiceTier>(tier, true, out var parsed) ? parsed : ServiceTier.Unknown,
            ProviderPricingJson.TryBoolean(root, "batch", out var batch) && batch,
            Text("region"),
            ProviderPricingJson.TryInt64(root, "model_context_window", out var context) && context >= 0 ? context : null
        )
        {
            SourceServiceTier = tier,
            Speed = Text("speed"),
            InferenceGeo = Text("inference_geo"),
        };
    }

    public static CacheDurationObservation ObserveCacheDurations(UsageEvent usage)
    {
        using var json = ProviderPricingJson.Evidence(usage.RawPayload);
        long? Count(string name)
        {
            var root = json.RootElement;
            return
                root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("cache_creation", out var creation)
                && creation.ValueKind == JsonValueKind.Object
                && creation.TryGetProperty(name, out var observed)
                && observed.ValueKind == JsonValueKind.Number
                && observed.TryGetInt64(out var count)
                && count >= 0
                ? count
                : null;
        }
        return new(Count("ephemeral_5m_input_tokens"), Count("ephemeral_1h_input_tokens"));
    }

    public static string RateKey<T>(T entry)
    {
        // PostgreSQL jsonb can reorder properties and normalize numeric lexemes. Identity must survive that round-trip.
        using var document = JsonDocument.Parse(PricingCatalogJson.Serialize(entry));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteEntry(writer, document.RootElement);
        }
        return AuditCanonicalJson.ContentHash(stream.ToArray());
    }

    private static void WriteEntry(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteEntry(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteEntry(writer, item);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(
                    value.GetDecimal().ToString("0.############################", CultureInfo.InvariantCulture)
                );
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    public static CalculatedPrice Result<T>(
        UsagePriceQuote quote,
        T entry,
        PricingDimensions dimensions,
        LocalDate date
    ) => new(quote, RateKey(entry), dimensions, new(date.Year, date.Month, date.Day));

    public static string RevisionDigest(UsageEvent usage)
    {
        using var json = ProviderPricingJson.Evidence(usage.RawPayload);
        long? Cache(string name) =>
            ProviderPricingJson.TryNestedInt64(json.RootElement, "cache_creation", name, out var count) ? count : null;
        return AuditCanonicalJson.ContentHash(
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    usage.Provider,
                    OccurredAt = usage.OccurredAt.ToString(),
                    usage.Model,
                    usage.InputTokens,
                    usage.OutputTokens,
                    usage.CacheReadTokens,
                    usage.CacheWriteTokens,
                    usage.CacheWrite1hTokens,
                    usage.ThoughtTokens,
                    usage.SourceId,
                    usage.SourceKind,
                    usage.UsageScope,
                    usage.CostBasis,
                    Dimensions = Dimensions(usage),
                    ObservedCache5m = Cache("ephemeral_5m_input_tokens"),
                    ObservedCache1h = Cache("ephemeral_1h_input_tokens"),
                }
            )
        );
    }

    private static bool Safe(string value) =>
        value.Length is > 0 and <= 160
        && !value.Contains("..", StringComparison.Ordinal)
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ' ');
}
