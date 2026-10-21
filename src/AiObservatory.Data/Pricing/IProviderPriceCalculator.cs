using System.Text.Json;
using AiObservatory.Data.Entities;

namespace AiObservatory.Data.Pricing;

public sealed record UsagePriceQuote(decimal CostUsd, decimal? CacheSavingsUsd);

public interface IProviderPriceCalculator
{
    Provider Provider { get; }
    UsagePriceQuote? Calculate(UsageEvent usage, string normalizedCatalog);
}

internal static class ProviderPricingJson
{
    private static readonly JsonDocumentOptions EvidenceOptions = new() { AllowDuplicateProperties = false };

    public static JsonDocument Evidence(string json) => JsonDocument.Parse(json, EvidenceOptions);

    public static bool TryString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (root.ValueKind == JsonValueKind.Object)
        {
            return TryObjectString(root, name, out value);
        }

        if (root.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        string? observed = null;
        foreach (var item in root.EnumerateArray())
        {
            if (!TryObjectString(item, name, out var current))
            {
                return false;
            }

            if (observed is not null && !string.Equals(observed, current, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            observed = current;
        }

        value = observed ?? string.Empty;
        return observed is not null;
    }

    public static bool TryBoolean(JsonElement root, string name, out bool value)
    {
        value = false;
        if (
            root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(name, out var property)
            || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
        )
        {
            return false;
        }

        value = property.GetBoolean();
        return true;
    }

    public static bool TryInt64(JsonElement root, string name, out long value)
    {
        value = 0;
        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(name, out var property)
            && property.TryGetInt64(out value);
    }

    public static bool TryNestedInt64(JsonElement root, string parent, string name, out long value)
    {
        value = 0;
        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(parent, out var nested)
            && nested.ValueKind == JsonValueKind.Object
            && nested.TryGetProperty(name, out var property)
            && property.TryGetInt64(out value);
    }

    private static bool TryObjectString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (
            root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString())
        )
        {
            return false;
        }

        value = property.GetString()!;
        return true;
    }
}
