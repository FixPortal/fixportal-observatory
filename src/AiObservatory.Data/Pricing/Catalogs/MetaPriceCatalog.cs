using NodaTime;

namespace AiObservatory.Data.Pricing.Catalogs;

/// <summary>
/// Meta model prices as OpenRouter bills them. Meta's own docs publish a list price for a
/// direct API this estate cannot reach; OpenRouter is the route that actually charges, so
/// its catalogue is the rate a Meta row here was really billed at. The model ids are
/// OpenRouter's (<c>meta/muse-spark-1.3</c>), which is what the CLI emits — Meta's direct
/// ids drop the prefix and are deliberately not aliased to these, because whether the two
/// name the same weights is not observable from here.
/// </summary>
public sealed record MetaPriceCatalog(
    string Currency,
    string SourceUrl,
    Instant RetrievedAt,
    IReadOnlyList<MetaPriceEntry> Entries
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
            throw new InvalidDataException("Meta pricing must be USD and have an HTTPS source URL.");
        }

        if (Entries is null || Entries.Count == 0)
        {
            throw new InvalidDataException("Meta pricing must contain entries.");
        }

        var effectiveDates = new Dictionary<string, LocalDate>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Entries)
        {
            // A free or unpriced endpoint is a real OpenRouter state, but it is not a rate this
            // catalog can price against, so it is rejected at the boundary instead of stored as
            // a zero that would read as "this usage was free".
            if (
                string.IsNullOrWhiteSpace(entry.Model)
                || entry.Input <= 0
                || entry.Output <= 0
                || entry.CacheRead is <= 0
                || entry.CacheRead > entry.Input
            )
            {
                throw new InvalidDataException("Meta pricing contains an incomplete or non-positive entry.");
            }

            if (effectiveDates.TryGetValue(entry.Model, out var previous) && entry.EffectiveFrom <= previous)
            {
                throw new InvalidDataException("Meta effective windows must be unique and ordered.");
            }

            effectiveDates[entry.Model] = entry.EffectiveFrom;
        }
    }

    /// <summary>
    /// Exact model id only. OpenRouter distinguishes variants by suffix
    /// (<c>-contributor</c>, <c>:batch</c>) at rates an order of magnitude apart, so a prefix
    /// match would price contributor usage at full rate and vice versa.
    /// </summary>
    public MetaPriceEntry? Resolve(string model, LocalDate usageDate)
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

public sealed record MetaPriceEntry(
    string Model,
    LocalDate EffectiveFrom,
    bool EffectiveDateIsProviderDeclared,
    decimal Input,
    decimal Output,
    decimal? CacheRead
);
