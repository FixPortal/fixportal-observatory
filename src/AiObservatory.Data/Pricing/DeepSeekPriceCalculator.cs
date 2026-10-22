using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing.Catalogs;

namespace AiObservatory.Data.Pricing;

/// <summary>
/// Prices DeepSeek usage from the bundled list catalog. The cache-hit input rate is
/// <c>CacheRead</c>; cache writes bill as ordinary (cache-miss) input.
/// <para>
/// Every figure is priced at the STANDARD (peak) tier and is therefore an UPPER BOUND for token charges only.
/// Search and Fetch calls from Codex are billed per request from 2026-11-01 and are not in token
/// telemetry, so the total can still be understated.
/// DeepSeek halves its rates off-peak and on Chinese public holidays, but the sweep posts one
/// cumulative snapshot per (date, model) whose <c>OccurredAt</c> is only the latest session end
/// of the group, so the time of day cannot be attributed to the day's tokens. Rating by it
/// would be off by up to 2x in either direction and could understate; a deterministic ceiling
/// is preferred. Billed invoices are the truth for DeepSeek.
/// </para>
/// </summary>
public sealed class DeepSeekPriceCalculator : IProviderPriceCalculator
{
    public Provider Provider => Provider.DeepSeek;

    public UsagePriceQuote? Calculate(UsageEvent usage, string normalizedCatalog) =>
        CalculateWithEvidence(usage, normalizedCatalog)?.Quote;

    public CalculatedPrice? CalculateWithEvidence(UsageEvent usage, string normalizedCatalog)
    {
        if (string.IsNullOrWhiteSpace(usage.Model))
        {
            return null;
        }

        var catalog = PricingCatalogJson.Deserialize<DeepSeekPriceCatalog>(normalizedCatalog);
        var pricingDate =
            usage.CostBasis == CostBasis.Notional ? catalog.RetrievedAt.InUtc().Date : usage.OccurredAt.InUtc().Date;
        var entry = catalog.Resolve(usage.Model, pricingDate);
        if (entry is null)
        {
            return null;
        }

        var cacheRead = usage.CacheReadTokens ?? 0;
        var uncached = usage.InputTokens + (usage.CacheWriteTokens ?? 0);
        var cost =
            PerMillion(uncached, entry.Input)
            + PerMillion(cacheRead, entry.CacheRead)
            + PerMillion(usage.OutputTokens, entry.Output);
        var savings = PerMillion(cacheRead, entry.Input - entry.CacheRead);
        return PricingEvidence.Result(
            new UsagePriceQuote(cost, savings),
            entry,
            PricingEvidence.Dimensions(usage),
            pricingDate
        );
    }

    private static decimal PerMillion(long tokens, decimal rate) => tokens / 1_000_000m * rate;
}
