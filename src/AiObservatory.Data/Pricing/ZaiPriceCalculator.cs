using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing.Catalogs;

namespace AiObservatory.Data.Pricing;

public sealed class ZaiPriceCalculator : IProviderPriceCalculator
{
    public Provider Provider => Provider.Zai;

    public UsagePriceQuote? Calculate(UsageEvent usage, string normalizedCatalog)
    {
        if (string.IsNullOrWhiteSpace(usage.Model))
        {
            return null;
        }

        // The z.AI page carries no rate dimension beyond the model id, so there is no payload
        // flag to require. Notional (flat-plan) rows price at the catalog retrieval date.
        var catalog = PricingCatalogJson.Deserialize<ZaiPriceCatalog>(normalizedCatalog);
        var pricingDate =
            usage.CostBasis == CostBasis.Notional ? catalog.RetrievedAt.InUtc().Date : usage.OccurredAt.InUtc().Date;
        var entry = catalog.Resolve(usage.Model, pricingDate);
        if (entry is null)
        {
            return null;
        }

        var cacheRead = usage.CacheReadTokens ?? 0;
        if (cacheRead > 0 && entry.CacheRead is null)
        {
            return null;
        }

        // z.AI publishes no cache-write rate (storage is limited-time free), so cache writes
        // bill as ordinary input.
        var uncached = usage.InputTokens + (usage.CacheWriteTokens ?? 0);
        var cost =
            PerMillion(uncached, entry.Input)
            + PerMillion(cacheRead, entry.CacheRead ?? 0m)
            + PerMillion(usage.OutputTokens, entry.Output);
        var savings = PerMillion(cacheRead, entry.Input - (entry.CacheRead ?? 0m));
        return new UsagePriceQuote(cost, savings);
    }

    private static decimal PerMillion(long tokens, decimal rate) => tokens / 1_000_000m * rate;
}
