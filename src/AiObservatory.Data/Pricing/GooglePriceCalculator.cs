using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing.Catalogs;

namespace AiObservatory.Data.Pricing;

public sealed class GooglePriceCalculator : IProviderPriceCalculator
{
    public Provider Provider => Provider.Google;

    public UsagePriceQuote? Calculate(UsageEvent usage, string normalizedCatalog)
    {
        using var evidence = ProviderPricingJson.Evidence(usage.RawPayload);
        var isNotional = usage.CostBasis == CostBasis.Notional;
        var isDeveloperApi =
            ProviderPricingJson.TryString(evidence.RootElement, "service", out var service)
            && service.Equals("Gemini Developer API", StringComparison.OrdinalIgnoreCase);
        if (isNotional || isDeveloperApi)
        {
            return CalculateDeveloperApi(usage, evidence.RootElement, normalizedCatalog, isNotional);
        }

        if (
            !ProviderPricingJson.TryString(evidence.RootElement, "service", out service)
            || !ProviderPricingJson.TryString(evidence.RootElement, "sku_id", out var skuId)
            || !ProviderPricingJson.TryString(evidence.RootElement, "region", out var region)
            || !ProviderPricingJson.TryString(evidence.RootElement, "modality", out var modality)
            || !ProviderPricingJson.TryString(evidence.RootElement, "tier", out var tier)
            || !ProviderPricingJson.TryString(evidence.RootElement, "cache_lane", out var cacheLane)
            || !ProviderPricingJson.TryInt64(evidence.RootElement, "context_threshold", out var contextThreshold)
        )
        {
            return null;
        }

        var catalog = PricingCatalogJson.Deserialize<GooglePriceCatalog>(normalizedCatalog);
        var usageDate = usage.OccurredAt.InUtc().Date;
        var entry = catalog.Resolve(service, skuId, region, modality, tier, cacheLane, contextThreshold, usageDate);
        if (entry is null || !TryGetTokens(usage, modality, cacheLane, out var tokens))
        {
            return null;
        }

        var cost = tokens / 1_000_000m * entry.Rate;
        if (cacheLane.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return new UsagePriceQuote(cost, 0m);
        }

        var counterfactual = catalog
            .Entries.Where(candidate =>
                candidate.EffectiveFrom <= usageDate
                && string.Equals(candidate.Service, entry.Service, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.Region, entry.Region, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.Modality, entry.Modality, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.Tier, entry.Tier, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.CacheLane, "none", StringComparison.OrdinalIgnoreCase)
                && candidate.ContextThreshold == entry.ContextThreshold
            )
            .OrderByDescending(candidate => candidate.EffectiveFrom)
            .FirstOrDefault();
        return new UsagePriceQuote(
            cost,
            counterfactual is null ? null : tokens / 1_000_000m * (counterfactual.Rate - entry.Rate)
        );
    }

    private static UsagePriceQuote? CalculateDeveloperApi(
        UsageEvent usage,
        System.Text.Json.JsonElement evidence,
        string normalizedCatalog,
        bool isNotional
    )
    {
        if (string.IsNullOrWhiteSpace(usage.Model))
        {
            return null;
        }
        var hasTier = ProviderPricingJson.TryString(evidence, "tier", out var tier);
        var hasContext = ProviderPricingJson.TryString(evidence, "context", out var context);
        if (!isNotional && (!hasTier || !hasContext))
        {
            return null;
        }
        tier = hasTier ? tier : "standard";
        context = hasContext ? context : "short";

        var catalog = PricingCatalogJson.Deserialize<GeminiDeveloperPriceCatalog>(normalizedCatalog);
        var pricingDate = isNotional ? catalog.RetrievedAt.InUtc().Date : usage.OccurredAt.InUtc().Date;
        var entry = catalog.Resolve(usage.Model, tier, context, pricingDate);
        if (entry is null)
        {
            return null;
        }

        var input = usage.InputTokens / 1_000_000m * entry.Input;
        var cached = (usage.CacheReadTokens ?? 0) / 1_000_000m * entry.CachedInput;
        var output = (usage.OutputTokens + (usage.ThoughtTokens ?? 0)) / 1_000_000m * entry.Output;
        var savings = (usage.CacheReadTokens ?? 0) / 1_000_000m * (entry.Input - entry.CachedInput);
        return new UsagePriceQuote(input + cached + output, savings);
    }

    private static bool TryGetTokens(UsageEvent usage, string modality, string cacheLane, out long tokens)
    {
        cacheLane = cacheLane.ToLowerInvariant();
        modality = modality.ToLowerInvariant();
        if (cacheLane is "read" or "cache-read" or "hit")
        {
            tokens = usage.CacheReadTokens ?? 0;
            return true;
        }

        if (cacheLane is "write" or "cache-write")
        {
            tokens = usage.CacheWriteTokens ?? 0;
            return true;
        }

        if (!cacheLane.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            tokens = 0;
            return false;
        }

        if (modality.Contains("output", StringComparison.OrdinalIgnoreCase))
        {
            tokens = usage.OutputTokens;
            return true;
        }

        if (modality is "text" or "input" or "text-input" or "image" or "audio" or "video")
        {
            tokens = usage.InputTokens;
            return true;
        }

        tokens = 0;
        return false;
    }
}
