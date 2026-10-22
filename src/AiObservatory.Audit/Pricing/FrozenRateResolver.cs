using AiObservatory.Audit.Contracts;

namespace AiObservatory.Audit.Pricing;

/// <summary>Only resolves the declared retained catalog. A content digest is not publication attestation.</summary>
public static class FrozenRateResolver
{
    public static FrozenPricing? Catalog(AuditInput input, FrozenEvent e) =>
        e.PricingApplication is { } app
            ? input.PricingCatalogs.SingleOrDefault(c =>
                c.Id == app.SnapshotId
                && c.ContentHash == app.SnapshotHash
                && string.Equals(c.Provider, e.Provider, StringComparison.OrdinalIgnoreCase)
            )
            : null;

    public static FrozenPriceRate? Rate(FrozenPricing catalog, FrozenEvent e, PriceMeter meter)
    {
        var app = e.PricingApplication!;
        var dimensions = app.Dimensions;
        var candidates = catalog
            .Rates.Where(r =>
                r.CatalogEntryKey == app.RuleId
                && r.Meter == meter
                && ModelMatches(catalog.Provider, r, e.Model)
                && r.EffectiveFrom <= app.PricingDate
                && (r.EffectiveTo == null || r.EffectiveTo >= app.PricingDate)
                && Matches(r.Dimensions, dimensions)
            )
            .ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    public static (FrozenPricing Catalog, FrozenEvent Event)? DeclaredFallback(AuditInput input, FrozenEvent e)
    {
        if (e.PricingApplication != null || e.SourceDimensions == null)
        {
            return null;
        }
        var choices = new List<(FrozenPricing, FrozenEvent)>();
        foreach (
            var catalog in input.PricingCatalogs.Where(c =>
                string.Equals(c.Provider, e.Provider, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            var date =
                e.Provenance.CostBasis == AccountingBasis.Notional
                    ? DateOnly.FromDateTime(catalog.RetrievedAt.UtcDateTime)
                    : DateOnly.FromDateTime(e.OccurredAt.UtcDateTime);
            var keys = catalog
                .Rates.Where(r =>
                    r.CatalogEntryKey != null
                    && ModelMatches(catalog.Provider, r, e.Model)
                    && Matches(r.Dimensions, e.SourceDimensions)
                    && r.EffectiveFrom <= date
                    && (r.EffectiveTo == null || r.EffectiveTo >= date)
                )
                .Select(r => r.CatalogEntryKey!)
                .Distinct()
                .ToArray();
            foreach (var key in keys)
            {
                choices.Add(
                    (
                        catalog,
                        e with
                        {
                            PricingApplication = new(catalog.Id, catalog.ContentHash, key, e.SourceDimensions, date)
                            {
                                Kind = "catalog",
                            },
                        }
                    )
                );
            }
        }
        return choices.Count == 1 ? choices[0] : null;
    }

    private static bool ModelMatches(string provider, FrozenPriceRate rate, string? model) =>
        model != null
        && (
            rate.Model == model
            || rate.Aliases.Contains(model)
            || provider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)
                && model.StartsWith(rate.Model + "-", StringComparison.OrdinalIgnoreCase)
        );

    private static bool Matches(PricingDimensions rate, PricingDimensions app) =>
        rate.Tier == app.Tier
        && rate.Batch == app.Batch
        && rate.Region == app.Region
        && rate.Processing == app.Processing
        && rate.Context == app.Context
        && rate.Service == app.Service
        && rate.Sku == app.Sku
        && rate.Modality == app.Modality
        && rate.CacheLane == app.CacheLane
        && rate.HighSpeed == app.HighSpeed
        && rate.LongContext == app.LongContext
        && rate.SourceServiceTier == app.SourceServiceTier
        && rate.Speed == app.Speed
        && rate.InferenceGeo == app.InferenceGeo;
}
