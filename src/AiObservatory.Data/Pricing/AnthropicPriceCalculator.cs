using System.Text.Json;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing.Catalogs;

namespace AiObservatory.Data.Pricing;

public sealed class AnthropicPriceCalculator : IProviderPriceCalculator
{
    public Provider Provider => Provider.Anthropic;

    public UsagePriceQuote? Calculate(UsageEvent usage, string normalizedCatalog) =>
        CalculateWithEvidence(usage, normalizedCatalog)?.Quote;

    public CalculatedPrice? CalculateWithEvidence(UsageEvent usage, string normalizedCatalog) =>
        CalculateWithEvidence(usage, normalizedCatalog, allowHistoricalFlatClaim: false);

    internal CalculatedPrice? CalculateWithEvidence(
        UsageEvent usage,
        string normalizedCatalog,
        bool allowHistoricalFlatClaim
    )
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
        var entry = ResolveEntry(usage, evidence.RootElement, catalog, pricingDate, allowHistoricalFlatClaim);
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
        var cacheWrite1H = usage.CacheWrite1hTokens ?? 0;
        var cacheWrite5M = cacheWrite - cacheWrite1H;
        if (cacheWrite > 0 && !HasExactCacheDurations(evidence.RootElement, cacheWrite5M, cacheWrite1H))
        {
            if (!isNotional)
            {
                return null;
            }
            cacheWrite5M = cacheWrite;
            cacheWrite1H = 0;
        }

        var cacheRead = usage.CacheReadTokens ?? 0;
        var pricingModifier = inputRate.Value / entry.Input;
        var cacheReadRate = entry.CacheRead * pricingModifier;
        var cacheWrite5MRate = entry.CacheWrite5m * pricingModifier;
        var cacheWrite1HRate = entry.CacheWrite1h * pricingModifier;
        var cost =
            (
                PerMillion(usage.InputTokens, inputRate.Value)
                + PerMillion(usage.OutputTokens, outputRate.Value)
                + PerMillion(cacheRead, cacheReadRate)
                + PerMillion(cacheWrite5M, cacheWrite5MRate)
                + PerMillion(cacheWrite1H, cacheWrite1HRate)
            ) * multiplier.Value;
        var cacheSavings =
            (
                PerMillion(cacheRead + cacheWrite, inputRate.Value)
                - PerMillion(cacheRead, cacheReadRate)
                - PerMillion(cacheWrite5M, cacheWrite5MRate)
                - PerMillion(cacheWrite1H, cacheWrite1HRate)
            ) * multiplier.Value;
        return PricingEvidence.Result(
            new UsagePriceQuote(cost, cacheSavings),
            entry,
            PricingEvidence.Dimensions(usage) with
            {
                Tier =
                    tier == "standard"
                        ? AiObservatory.Audit.Contracts.ServiceTier.Standard
                        : AiObservatory.Audit.Contracts.ServiceTier.Unknown,
                SourceServiceTier = tier,
                Batch = tier == "batch",
                Speed = speed,
                InferenceGeo = geography,
                Context = entry.Context,
            },
            pricingDate
        );
    }

    private static AnthropicPriceEntry? ResolveEntry(
        UsageEvent usage,
        JsonElement evidence,
        AnthropicPriceCatalog catalog,
        NodaTime.LocalDate date,
        bool allowHistoricalFlatClaim
    )
    {
        var context = PricingEvidence.ClaudePromptContext(usage, evidence);
        var entry = catalog.Resolve(usage.Model!, date, context);
        if (entry is null || entry.Context is not null)
        {
            return entry;
        }
        var historicalFlatClaim =
            allowHistoricalFlatClaim || PricingEvidence.AllowsHistoricalClaudeFlatQuote(usage, evidence, catalog, date);
        // Active Haiku5.5 history cannot authorize a different retained family merely
        // because that family's explicit alias also matches the requested model name.
        if (historicalFlatClaim && !entry.ModelPrefix.Equals("claude-haiku-5-5", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return
            PricingEvidence.RequiresProspectiveClaudePromptContext(usage, evidence, catalog, date)
            && !historicalFlatClaim
            ? null
            : entry;
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

    private static bool HasExactCacheDurations(JsonElement evidence, long expected5M, long expected1H) =>
        ProviderPricingJson.TryNestedInt64(evidence, "cache_creation", "ephemeral_5m_input_tokens", out var observed5M)
        && ProviderPricingJson.TryNestedInt64(
            evidence,
            "cache_creation",
            "ephemeral_1h_input_tokens",
            out var observed1H
        )
        && observed5M == expected5M
        && observed1H == expected1H;

    private static decimal PerMillion(long tokens, decimal rate) => tokens / 1_000_000m * rate;
}
