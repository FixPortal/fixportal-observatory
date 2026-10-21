using NodaTime;

namespace AiObservatory.Data.Pricing.Catalogs;

/// <summary>
/// DeepSeek list prices from its public pricing page. Bundled only: the page is not a
/// machine-readable feed, so the catalog is refreshed by editing <c>Bundled/deepseek.json</c>.
/// Rates are the STANDARD (peak) tier, so a figure priced from this catalog is an UPPER BOUND
/// for token charges only (per-request Search and Fetch billing from 2026-11-01 is not in token
/// telemetry, so a total can still be understated):
/// the vendor halves every rate off-peak and on Chinese public holidays, which a daily
/// cumulative snapshot cannot express. The off-peak rates are deliberately not stored.
/// </summary>
public sealed record DeepSeekPriceCatalog(
    string Currency,
    string SourceUrl,
    Instant RetrievedAt,
    IReadOnlyList<DeepSeekPriceEntry> Entries
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
            throw new InvalidDataException("DeepSeek pricing must be USD and have an HTTPS source URL.");
        }

        if (Entries is null || Entries.Count == 0)
        {
            throw new InvalidDataException("DeepSeek pricing must contain entries.");
        }

        var effectiveDates = new Dictionary<string, LocalDate>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Entries)
        {
            if (
                string.IsNullOrWhiteSpace(entry.Model)
                || entry.Input <= 0
                || entry.Output <= 0
                || entry.CacheRead <= 0
                || entry.CacheRead > entry.Input
            )
            {
                throw new InvalidDataException("DeepSeek pricing contains an incomplete or non-positive entry.");
            }

            if (effectiveDates.TryGetValue(entry.Model, out var previous) && entry.EffectiveFrom <= previous)
            {
                throw new InvalidDataException("DeepSeek effective windows must be unique and ordered.");
            }

            effectiveDates[entry.Model] = entry.EffectiveFrom;
        }
    }

    /// <summary>
    /// Exact model id only. DeepSeek retired aliases are billed at the Flash price, but the
    /// page names them only in prose, so they are not aliased here.
    /// </summary>
    public DeepSeekPriceEntry? Resolve(string model, LocalDate usageDate)
    {
        return EffectiveWindow
            .ApplicableAt(
                Entries.Where(entry => string.Equals(entry.Model, model, StringComparison.OrdinalIgnoreCase)),
                usageDate,
                entry => entry.EffectiveFrom,
                entry => entry.EffectiveDateIsProviderDeclared
            )
            .OrderByDescending(entry => entry.EffectiveFrom)
            .FirstOrDefault();
    }
}

/// <summary>Standard-tier rates per million tokens. <c>CacheRead</c> is the cache-hit input rate.</summary>
public sealed record DeepSeekPriceEntry(
    string Model,
    LocalDate EffectiveFrom,
    bool EffectiveDateIsProviderDeclared,
    decimal Input,
    decimal Output,
    decimal CacheRead
);
