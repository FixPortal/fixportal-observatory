using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing.Catalogs;

namespace AiObservatory.Data.Pricing;

public sealed class KimiPriceCalculator : IProviderPriceCalculator
{
    public Provider Provider => Provider.Moonshot;

    public UsagePriceQuote? Calculate(UsageEvent usage, string normalizedCatalog)
    {
        if (string.IsNullOrWhiteSpace(usage.Model))
        {
            return null;
        }

        using var evidence = ProviderPricingJson.Evidence(usage.RawPayload);
        var isNotional = usage.CostBasis == CostBasis.Notional;
        var model = isNotional ? NormalizeModel(usage.Model) : usage.Model;
        var hasHighSpeed = ProviderPricingJson.TryBoolean(evidence.RootElement, "high_speed", out var highSpeed);
        var hasBatch = ProviderPricingJson.TryBoolean(evidence.RootElement, "batch", out var batch);
        if (!isNotional && (!hasHighSpeed || !hasBatch))
        {
            return null;
        }

        // The model suffix and the payload flag both claim the speed lane. When they disagree the
        // event is mispriced either way — a "…-highspeed" model reported with high_speed:false
        // prefix-matches the standard entry and prices high-speed usage at the standard rate —
        // so refuse and surface it instead of guessing.
        if (
            !isNotional
            && hasHighSpeed
            && model.EndsWith("-highspeed", StringComparison.OrdinalIgnoreCase) != highSpeed
        )
        {
            return null;
        }

        if (!hasHighSpeed)
        {
            highSpeed = model.EndsWith("-highspeed", StringComparison.OrdinalIgnoreCase);
        }

        var catalog = PricingCatalogJson.Deserialize<KimiPriceCatalog>(normalizedCatalog);
        var pricingDate = isNotional ? catalog.RetrievedAt.InUtc().Date : usage.OccurredAt.InUtc().Date;
        var entry = catalog.Resolve(model, highSpeed, pricingDate);
        if (entry is null || batch && entry.BatchMultiplier is null)
        {
            return null;
        }

        var multiplier = batch ? entry.BatchMultiplier!.Value : 1m;
        var cacheRead = usage.CacheReadTokens ?? 0;
        var cacheMiss = usage.InputTokens + (usage.CacheWriteTokens ?? 0);
        var cost =
            (
                PerMillion(cacheMiss, entry.CacheMiss)
                + PerMillion(cacheRead, entry.CacheHit)
                + PerMillion(usage.OutputTokens, entry.Output)
            ) * multiplier;
        var savings = PerMillion(cacheRead, entry.CacheMiss - entry.CacheHit) * multiplier;
        return new UsagePriceQuote(cost, savings);
    }

    private static decimal PerMillion(long tokens, decimal rate) => tokens / 1_000_000m * rate;

    private static string NormalizeModel(string model) =>
        model.ToLowerInvariant() switch
        {
            "kimi-code/kimi-for-coding" or "kimi-for-coding" => "kimi-k2.7-code",
            "kimi-code/kimi-for-coding-highspeed" or "kimi-for-coding-highspeed" => "kimi-k2.7-code-highspeed",
            "kimi-code/k3" or "kimi-code/k3-256k" or "k3" or "k3-256k" => "kimi-k3",
            _ => model,
        };
}
