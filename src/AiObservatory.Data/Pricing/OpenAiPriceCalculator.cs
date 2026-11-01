using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing.Catalogs;
using NodaTime;

namespace AiObservatory.Data.Pricing;

public sealed class OpenAiPriceCalculator : IProviderPriceCalculator
{
    public Provider Provider => Provider.OpenAI;

    public UsagePriceQuote? Calculate(UsageEvent usage, string normalizedCatalog) =>
        CalculateWithEvidence(usage, normalizedCatalog)?.Quote;

    /// <summary>
    /// True when this catalog carries the model but deliberately prices no cache-write rate for it
    /// (the current document prices writes by duration, which usage does not report). That is a
    /// refusal by the catalog, not an absent model, so a caller must not fall through to an older
    /// snapshot whose generic write rate was superseded.
    /// </summary>
    public bool RefusesCacheWrite(UsageEvent usage, string normalizedCatalog) =>
        (usage.CacheWriteTokens ?? 0) > 0 && Lookup(usage, normalizedCatalog) is { Entry.CacheWrite: null };

    public CalculatedPrice? CalculateWithEvidence(UsageEvent usage, string normalizedCatalog)
    {
        if (Lookup(usage, normalizedCatalog) is not { Entry: { } entry } found)
        {
            return null;
        }

        var cacheRead = usage.CacheReadTokens ?? 0;
        var cacheWrite = usage.CacheWriteTokens ?? 0;
        if (cacheRead > 0 && entry.CachedInput is null || cacheWrite > 0 && entry.CacheWrite is null)
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
        return PricingEvidence.Result(
            new UsagePriceQuote(cost, cacheSavings),
            entry,
            PricingEvidence.Dimensions(usage) with
            {
                Tier = Enum.TryParse<AiObservatory.Audit.Contracts.ServiceTier>(
                    found.Processing,
                    true,
                    out var resolvedTier
                )
                    ? resolvedTier
                    : AiObservatory.Audit.Contracts.ServiceTier.Unknown,
                Processing = found.Processing,
                Context = found.Context,
                Region = found.Region,
            },
            found.PricingDate
        );
    }

    private static Found? Lookup(UsageEvent usage, string normalizedCatalog)
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
        return new Found(
            catalog.Resolve(usage.Model, processing, context, region, pricingDate),
            processing,
            context,
            region,
            pricingDate
        );
    }

    private static decimal PerMillion(long tokens, decimal rate) => tokens / 1_000_000m * rate;

    private sealed record Found(
        OpenAiPriceEntry? Entry,
        string Processing,
        string Context,
        string Region,
        LocalDate PricingDate
    );
}
