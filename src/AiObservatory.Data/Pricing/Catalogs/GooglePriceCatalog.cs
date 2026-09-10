using NodaTime;

namespace AiObservatory.Data.Pricing.Catalogs;

public sealed record GooglePriceCatalog(
    string Currency,
    string SourceUrl,
    Instant RetrievedAt,
    IReadOnlyList<GooglePriceEntry> Entries
)
{
    public void Validate()
    {
        if (
            Currency != "USD"
            || !Uri.TryCreate(SourceUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
        )
        {
            throw new InvalidDataException("Google pricing must be USD and have an HTTPS source URL.");
        }

        if (Entries is null)
        {
            throw new InvalidDataException("Google pricing entries are required.");
        }

        var effectiveDates =
            new Dictionary<
                (
                    string Alias,
                    string SkuId,
                    string Region,
                    string Modality,
                    string Tier,
                    string CacheLane,
                    long Context
                ),
                LocalDate
            >();
        foreach (var entry in Entries)
        {
            if (
                string.IsNullOrWhiteSpace(entry.Service)
                || string.IsNullOrWhiteSpace(entry.SkuId)
                || string.IsNullOrWhiteSpace(entry.SkuName)
                || string.IsNullOrWhiteSpace(entry.Description)
                || entry.Aliases is null
                || entry.Aliases.Count == 0
                || entry.Aliases.Any(string.IsNullOrWhiteSpace)
                || entry.Aliases.Distinct(StringComparer.OrdinalIgnoreCase).Count() != entry.Aliases.Count
                || string.IsNullOrWhiteSpace(entry.Region)
                || string.IsNullOrWhiteSpace(entry.Modality)
                || string.IsNullOrWhiteSpace(entry.Tier)
                || string.IsNullOrWhiteSpace(entry.CacheLane)
                || entry.ContextThreshold < 0
                || string.IsNullOrWhiteSpace(entry.PricingUnit)
                || string.IsNullOrWhiteSpace(entry.PricingUnitDescription)
                || string.IsNullOrWhiteSpace(entry.BaseUnit)
                || string.IsNullOrWhiteSpace(entry.BaseUnitDescription)
                || entry.BaseUnitConversionFactor <= 0
                || entry.DisplayQuantity <= 0
                || string.IsNullOrWhiteSpace(entry.GeoTaxonomyType)
                || entry.ServiceRegions is null
                || entry.ServiceRegions.Count == 0
                || entry.ServiceRegions.Any(string.IsNullOrWhiteSpace)
                || entry.ServiceRegions.Distinct(StringComparer.Ordinal).Count() != entry.ServiceRegions.Count
                || entry.GeoTaxonomyRegions is null
                || entry.GeoTaxonomyRegions.Any(string.IsNullOrWhiteSpace)
                || entry.GeoTaxonomyRegions.Distinct(StringComparer.Ordinal).Count() != entry.GeoTaxonomyRegions.Count
                || string.IsNullOrWhiteSpace(entry.UnitPriceCurrencyCode)
                || string.IsNullOrWhiteSpace(entry.AggregationLevel)
                || string.IsNullOrWhiteSpace(entry.AggregationInterval)
                || entry.AggregationCount <= 0
                || entry.TierStartUsageAmount < 0
                || entry.Rate <= 0
                || !entry.EffectiveDateIsProviderDeclared
                || entry.EffectiveTime.InUtc().Date != entry.EffectiveFrom
                || entry.UnitPriceCurrencyCode != Currency
                || entry.CurrencyConversionRate != 1m
                || entry.UnitPriceNanos is < -999_999_999 or > 999_999_999
                || Math.Sign(entry.UnitPriceUnits) * Math.Sign(entry.UnitPriceNanos) < 0
                || entry.Rate != (entry.UnitPriceUnits + entry.UnitPriceNanos / 1_000_000_000m) * 1_000_000m
                || entry.PricingUnit == entry.BaseUnit && entry.BaseUnitConversionFactor != 1m
                || !HasValidGeography(entry)
                || !HasValidAggregation(entry)
            )
            {
                throw new InvalidDataException("Google pricing contains an incomplete or non-positive entry.");
            }

            foreach (var alias in entry.Aliases.Prepend(entry.Service).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var key = (
                    alias.ToUpperInvariant(),
                    entry.SkuId,
                    entry.Region.ToUpperInvariant(),
                    entry.Modality.ToUpperInvariant(),
                    entry.Tier.ToUpperInvariant(),
                    entry.CacheLane.ToUpperInvariant(),
                    entry.ContextThreshold
                );
                if (effectiveDates.TryGetValue(key, out var previous) && entry.EffectiveFrom <= previous)
                {
                    throw new InvalidDataException("Google effective windows must be unique and ordered.");
                }

                effectiveDates[key] = entry.EffectiveFrom;
            }
        }
    }

    private static bool HasValidGeography(GooglePriceEntry entry) =>
        entry.GeoTaxonomyType switch
        {
            "GLOBAL" => entry.Region == "global"
                && entry.GeoTaxonomyRegions.Count == 0
                && entry.ServiceRegions.Contains("global", StringComparer.Ordinal),
            "REGIONAL" => entry.Region != "global"
                && entry.GeoTaxonomyRegions.Count == 1
                && entry.GeoTaxonomyRegions.Contains(entry.Region, StringComparer.Ordinal)
                && entry.ServiceRegions.Contains(entry.Region, StringComparer.Ordinal),
            "MULTI_REGIONAL" => entry.Region != "global"
                && entry.GeoTaxonomyRegions.Count >= 2
                && entry.GeoTaxonomyRegions.Contains(entry.Region, StringComparer.Ordinal)
                && entry.ServiceRegions.Contains(entry.Region, StringComparer.Ordinal),
            _ => false,
        };

    private static bool HasValidAggregation(GooglePriceEntry entry) =>
        entry.AggregationLevel is "ACCOUNT" or "PROJECT" && entry.AggregationInterval is "DAILY" or "MONTHLY";

    public GooglePriceEntry? Resolve(
        string service,
        string skuId,
        string region,
        string modality,
        string tier,
        string cacheLane,
        long contextThreshold,
        LocalDate usageDate
    )
    {
        return Entries
            .Where(entry =>
                entry.EffectiveFrom <= usageDate
                && entry
                    .Aliases.Prepend(entry.Service)
                    .Any(alias => string.Equals(alias, service, StringComparison.OrdinalIgnoreCase))
                && string.Equals(entry.SkuId, skuId, StringComparison.Ordinal)
                && string.Equals(entry.Region, region, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.Modality, modality, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.Tier, tier, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.CacheLane, cacheLane, StringComparison.OrdinalIgnoreCase)
                && entry.ContextThreshold == contextThreshold
            )
            .OrderByDescending(entry => entry.EffectiveFrom)
            .FirstOrDefault();
    }
}

public sealed record GooglePriceEntry(
    string Service,
    string SkuId,
    string SkuName,
    string Description,
    IReadOnlyList<string> Aliases,
    LocalDate EffectiveFrom,
    bool EffectiveDateIsProviderDeclared,
    Instant EffectiveTime,
    string Region,
    string GeoTaxonomyType,
    IReadOnlyList<string> ServiceRegions,
    IReadOnlyList<string> GeoTaxonomyRegions,
    string Modality,
    string Tier,
    string CacheLane,
    long ContextThreshold,
    string PricingUnit,
    string PricingUnitDescription,
    string BaseUnit,
    string BaseUnitDescription,
    decimal BaseUnitConversionFactor,
    decimal DisplayQuantity,
    decimal TierStartUsageAmount,
    string UnitPriceCurrencyCode,
    long UnitPriceUnits,
    int UnitPriceNanos,
    string AggregationLevel,
    string AggregationInterval,
    int AggregationCount,
    decimal CurrencyConversionRate,
    decimal Rate
);
