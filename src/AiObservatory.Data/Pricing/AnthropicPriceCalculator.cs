using System.Text.Json;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing.Catalogs;

namespace AiObservatory.Data.Pricing;

public sealed class AnthropicPriceCalculator : IProviderPriceCalculator
{
    public Provider Provider => Provider.Anthropic;

    public UsagePriceQuote? Calculate(UsageEvent usage, string normalizedCatalog)
    {
        if (string.IsNullOrWhiteSpace(usage.Model))
        {
            return null;
        }

        using var evidence = ProviderPricingJson.Evidence(usage.RawPayload);
        var isNotional = usage.CostBasis == CostBasis.Notional;
        if (!TryDimensions(evidence.RootElement, isNotional, out var tier, out var speed, out var geography))
        {
            return null;
        }

        var catalog = PricingCatalogJson.Deserialize<AnthropicPriceCatalog>(normalizedCatalog);
        var pricingDate = isNotional ? catalog.RetrievedAt.InUtc().Date : usage.OccurredAt.InUtc().Date;
        var entry = catalog.Resolve(usage.Model, pricingDate);
        if (entry is null)
        {
            return null;
        }

        decimal? inputRate = entry.Input;
        decimal? outputRate = entry.Output;
        if (tier == "batch")
        {
            inputRate = entry.BatchInput;
            outputRate = entry.BatchOutput;
        }
        else if (speed == "fast")
        {
            inputRate = entry.FastInput;
            outputRate = entry.FastOutput;
        }
        var multiplier = geography == "us" ? entry.UsInferenceMultiplier : 1m;
        if (inputRate is null || outputRate is null || multiplier is null)
        {
            return null;
        }

        var cacheWrite = usage.CacheWriteTokens ?? 0;
        var cacheWrite1h = usage.CacheWrite1hTokens ?? 0;
        var cacheWrite5m = cacheWrite - cacheWrite1h;
        if (cacheWrite > 0 && !HasExactCacheDurations(evidence.RootElement, cacheWrite5m, cacheWrite1h))
        {
            if (!isNotional)
            {
                return null;
            }
            cacheWrite5m = cacheWrite;
            cacheWrite1h = 0;
        }

        var cacheRead = usage.CacheReadTokens ?? 0;
        var pricingModifier = inputRate.Value / entry.Input;
        var cacheReadRate = entry.CacheRead * pricingModifier;
        var cacheWrite5mRate = entry.CacheWrite5m * pricingModifier;
        var cacheWrite1hRate = entry.CacheWrite1h * pricingModifier;
        var cost =
            (
                PerMillion(usage.InputTokens, inputRate.Value)
                + PerMillion(usage.OutputTokens, outputRate.Value)
                + PerMillion(cacheRead, cacheReadRate)
                + PerMillion(cacheWrite5m, cacheWrite5mRate)
                + PerMillion(cacheWrite1h, cacheWrite1hRate)
            ) * multiplier.Value;
        var cacheSavings =
            (
                PerMillion(cacheRead + cacheWrite, inputRate.Value)
                - PerMillion(cacheRead, cacheReadRate)
                - PerMillion(cacheWrite5m, cacheWrite5mRate)
                - PerMillion(cacheWrite1h, cacheWrite1hRate)
            ) * multiplier.Value;
        return new UsagePriceQuote(cost, cacheSavings);
    }

    private static bool TryDimensions(
        JsonElement evidence,
        bool useStandardDefaults,
        out string tier,
        out string speed,
        out string geography
    )
    {
        tier = string.Empty;
        speed = string.Empty;
        geography = string.Empty;
        var hasTier = ProviderPricingJson.TryString(evidence, "service_tier", out tier);
        var hasSpeed = ProviderPricingJson.TryString(evidence, "speed", out speed);
        var hasGeography = ProviderPricingJson.TryString(evidence, "inference_geo", out geography);
        if (!useStandardDefaults && (!hasTier || !hasSpeed || !hasGeography))
        {
            return false;
        }

        tier = StandardDefault(tier, hasTier, useStandardDefaults);
        speed = StandardDefault(speed, hasSpeed, useStandardDefaults);
        geography =
            !hasGeography
            || useStandardDefaults
                && (
                    geography.Equals("unknown", StringComparison.OrdinalIgnoreCase)
                    || geography.Equals("not_available", StringComparison.OrdinalIgnoreCase)
                )
                ? "global"
                : geography.ToLowerInvariant();
        return tier is "standard" or "batch"
            && speed is "standard" or "fast"
            && geography is "global" or "us"
            && (tier != "batch" || speed != "fast");
    }

    private static string StandardDefault(string value, bool observed, bool useStandardDefaults) =>
        !observed || useStandardDefaults && value.Equals("unknown", StringComparison.OrdinalIgnoreCase)
            ? "standard"
            : value.ToLowerInvariant();

    private static bool HasExactCacheDurations(JsonElement evidence, long expected5m, long expected1h) =>
        ProviderPricingJson.TryNestedInt64(evidence, "cache_creation", "ephemeral_5m_input_tokens", out var observed5m)
        && ProviderPricingJson.TryNestedInt64(
            evidence,
            "cache_creation",
            "ephemeral_1h_input_tokens",
            out var observed1h
        )
        && observed5m == expected5m
        && observed1h == expected1h;

    private static decimal PerMillion(long tokens, decimal rate) => tokens / 1_000_000m * rate;
}
