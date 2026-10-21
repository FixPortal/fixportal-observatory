using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing.Catalogs;

namespace AiObservatory.Data.Pricing;

public sealed class OpenAiPriceCalculator : IProviderPriceCalculator
{
    public Provider Provider => Provider.OpenAI;

    public UsagePriceQuote? Calculate(UsageEvent usage, string normalizedCatalog)
    {
        if (string.IsNullOrWhiteSpace(usage.Model))
        {
            return null;
        }

        using var evidence = ProviderPricingJson.Evidence(usage.RawPayload);
        var hasProcessing = ProviderPricingJson.TryString(evidence.RootElement, "processing", out var processing);
        var hasContext = ProviderPricingJson.TryString(evidence.RootElement, "context", out var context);
        var hasRegion = ProviderPricingJson.TryString(evidence.RootElement, "region", out var region);
        var isNotional = usage.CostBasis == CostBasis.Notional;
        if (!isNotional && !(hasProcessing && hasContext && hasRegion))
        {
            return null;
        }

        // Notional (subscription-covered) telemetry often carries no pricing dimensions —
        // e.g. a zero-usage local snapshot posts "{}" — so, matching the sibling calculators,
        // default to the documented standard public tier rather than dropping the price.
        processing = hasProcessing ? processing : "standard";
        context = hasContext ? context : "short";
        region = hasRegion ? region : "global";

        var catalog = PricingCatalogJson.Deserialize<OpenAiPriceCatalog>(normalizedCatalog);
        var pricingDate = isNotional ? catalog.RetrievedAt.InUtc().Date : usage.OccurredAt.InUtc().Date;
        var entry = catalog.Resolve(usage.Model, processing, context, region, pricingDate);
        var cacheRead = usage.CacheReadTokens ?? 0;
        var cacheWrite = usage.CacheWriteTokens ?? 0;
        if (entry is null || cacheRead > 0 && entry.CachedInput is null || cacheWrite > 0 && entry.CacheWrite is null)
        {
            return null;
        }

        var cost =
            PerMillion(usage.InputTokens, entry.Input)
            + PerMillion(usage.OutputTokens, entry.Output)
            + PerMillion(cacheRead, entry.CachedInput ?? 0m)
            + PerMillion(cacheWrite, entry.CacheWrite ?? 0m);
        var cacheSavings =
            PerMillion(cacheRead + cacheWrite, entry.Input)
            - PerMillion(cacheRead, entry.CachedInput ?? 0m)
            - PerMillion(cacheWrite, entry.CacheWrite ?? 0m);
        return new UsagePriceQuote(cost, cacheSavings);
    }

    private static decimal PerMillion(long tokens, decimal rate) => tokens / 1_000_000m * rate;
}
