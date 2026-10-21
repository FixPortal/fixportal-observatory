using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing.Catalogs;

namespace AiObservatory.Data.Pricing;

public sealed class MetaPriceCalculator : IProviderPriceCalculator
{
    public Provider Provider => Provider.Meta;

    public UsagePriceQuote? Calculate(UsageEvent usage, string normalizedCatalog)
    {
        if (string.IsNullOrWhiteSpace(usage.Model))
        {
            return null;
        }

        // OpenRouter carries no rate dimension beyond the model id: the contributor and batch
        // lanes are separate ids with their own rows, so there is no payload flag to require.
        var catalog = PricingCatalogJson.Deserialize<MetaPriceCatalog>(normalizedCatalog);
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
            // The endpoint publishes no cache-read rate, so cached tokens have no price. Pricing
            // them at the input rate would overstate; at zero would understate. Refuse instead.
            return null;
        }

        // OpenRouter publishes no cache-write rate for any Meta endpoint, so cache writes are
        // billed as ordinary input — which is what its own per-request cost breakdown reports.
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
