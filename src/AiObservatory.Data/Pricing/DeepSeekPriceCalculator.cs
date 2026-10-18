using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing.Catalogs;
using NodaTime;

namespace AiObservatory.Data.Pricing;

/// <summary>
/// Prices DeepSeek usage from the bundled list catalog. The cache-hit input rate is
/// <c>CacheRead</c>; cache writes bill as ordinary (cache-miss) input.
/// <para>
/// DeepSeek publishes a peak and an off-peak rate (off-peak is half). Peak is 01:00-04:00 and
/// 06:00-10:00 UTC Monday to Friday, excluding Chinese public holidays. The window is derived
/// from <c>OccurredAt</c> in UTC. Weekends and weekday hours outside the window are off-peak
/// whatever the holiday calendar says. A weekday hour inside the window is priced at the peak
/// rate: no holiday calendar is bundled, so on a Chinese public holiday that is an upper bound.
/// </para>
/// </summary>
public sealed class DeepSeekPriceCalculator : IProviderPriceCalculator
{
    public Provider Provider => Provider.DeepSeek;

    public UsagePriceQuote? Calculate(UsageEvent usage, string normalizedCatalog)
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

        var peak = IsPeak(usage.OccurredAt);
        var inputRate = peak ? entry.Input : entry.OffPeakInput;
        var outputRate = peak ? entry.Output : entry.OffPeakOutput;
        var cacheRate = peak ? entry.CacheRead : entry.OffPeakCacheRead;

        var cacheRead = usage.CacheReadTokens ?? 0;
        var uncached = usage.InputTokens + (usage.CacheWriteTokens ?? 0);
        var cost =
            PerMillion(uncached, inputRate)
            + PerMillion(cacheRead, cacheRate)
            + PerMillion(usage.OutputTokens, outputRate);
        var savings = PerMillion(cacheRead, inputRate - cacheRate);
        return new UsagePriceQuote(cost, savings);
    }

    private static bool IsPeak(Instant occurredAt)
    {
        var utc = occurredAt.InUtc();
        if (utc.DayOfWeek is IsoDayOfWeek.Saturday or IsoDayOfWeek.Sunday)
        {
            return false;
        }

        return utc.Hour is >= 1 and < 4 or >= 6 and < 10;
    }

    private static decimal PerMillion(long tokens, decimal rate) => tokens / 1_000_000m * rate;
}
