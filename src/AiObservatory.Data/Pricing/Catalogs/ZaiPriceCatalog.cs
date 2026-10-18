using NodaTime;

namespace AiObservatory.Data.Pricing.Catalogs;

/// <summary>
/// z.AI list prices from its public pricing page. Bundled only: the page is not a
/// machine-readable feed, so the catalog is refreshed by editing <c>Bundled/zai.json</c>.
/// Interactive GLM use is a flat plan, so rows priced from this catalog are notional.
/// </summary>
public sealed record ZaiPriceCatalog(
    string Currency,
    string SourceUrl,
    Instant RetrievedAt,
    IReadOnlyList<ZaiPriceEntry> Entries
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
            throw new InvalidDataException("z.AI pricing must be USD and have an HTTPS source URL.");
        }

        if (Entries is null || Entries.Count == 0)
        {
            throw new InvalidDataException("z.AI pricing must contain entries.");
        }

        var effectiveDates = new Dictionary<string, LocalDate>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Entries)
        {
            if (
                string.IsNullOrWhiteSpace(entry.Model)
                || entry.Input <= 0
                || entry.Output <= 0
                || entry.CacheRead is <= 0
                || entry.CacheRead > entry.Input
            )
            {
                throw new InvalidDataException("z.AI pricing contains an incomplete or non-positive entry.");
            }

            if (effectiveDates.TryGetValue(entry.Model, out var previous) && entry.EffectiveFrom <= previous)
            {
                throw new InvalidDataException("z.AI effective windows must be unique and ordered.");
            }

            effectiveDates[entry.Model] = entry.EffectiveFrom;
        }
    }

    /// <summary>Exact model id only; a variant such as <c>-flash</c> is a different, cheaper model.</summary>
    public ZaiPriceEntry? Resolve(string model, LocalDate usageDate)
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

public sealed record ZaiPriceEntry(
    string Model,
    LocalDate EffectiveFrom,
    bool EffectiveDateIsProviderDeclared,
    decimal Input,
    decimal Output,
    decimal? CacheRead
);
