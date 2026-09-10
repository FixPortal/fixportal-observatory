using NodaTime;

namespace AiObservatory.Data.Pricing.Catalogs;

public sealed record KimiPriceCatalog(
    string Currency,
    string SourceUrl,
    Instant RetrievedAt,
    IReadOnlyList<KimiPriceEntry> Entries
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
            throw new InvalidDataException("Kimi pricing must be USD and have an HTTPS source URL.");
        }

        if (Entries is null || Entries.Count == 0)
        {
            throw new InvalidDataException("Kimi pricing must contain entries.");
        }

        var effectiveDates = new Dictionary<string, LocalDate>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Entries)
        {
            if (
                string.IsNullOrWhiteSpace(entry.ModelPrefix)
                || entry.Aliases is null
                || entry.Aliases.Count == 0
                || entry.Aliases.Any(string.IsNullOrWhiteSpace)
                || entry.Aliases.Distinct(StringComparer.OrdinalIgnoreCase).Count() != entry.Aliases.Count
                || entry.CacheHit <= 0
                || entry.CacheMiss <= 0
                || entry.Output <= 0
                || entry.BatchMultiplier is <= 0
            )
            {
                throw new InvalidDataException("Kimi pricing contains an incomplete or non-positive entry.");
            }

            foreach (var alias in entry.Aliases.Prepend(entry.ModelPrefix).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var key = $"{alias}\u001f{entry.HighSpeed}";
                if (effectiveDates.TryGetValue(key, out var previous) && entry.EffectiveFrom <= previous)
                {
                    throw new InvalidDataException("Kimi effective windows must be unique and ordered.");
                }

                effectiveDates[key] = entry.EffectiveFrom;
            }
        }
    }

    public KimiPriceEntry? Resolve(string model, bool highSpeed, LocalDate usageDate)
    {
        return EffectiveWindow
            .ApplicableAt(
                Entries.Where(entry =>
                    entry.HighSpeed == highSpeed
                    && entry
                        .Aliases.Prepend(entry.ModelPrefix)
                        .Any(alias => model.StartsWith(alias, StringComparison.OrdinalIgnoreCase))
                ),
                usageDate,
                entry => entry.EffectiveFrom,
                entry => entry.EffectiveDateIsProviderDeclared
            )
            .OrderByDescending(entry =>
                entry
                    .Aliases.Prepend(entry.ModelPrefix)
                    .Where(alias => model.StartsWith(alias, StringComparison.OrdinalIgnoreCase))
                    .Max(alias => alias.Length)
            )
            .ThenByDescending(entry => entry.EffectiveFrom)
            .FirstOrDefault();
    }
}

public sealed record KimiPriceEntry(
    string ModelPrefix,
    IReadOnlyList<string> Aliases,
    LocalDate EffectiveFrom,
    bool EffectiveDateIsProviderDeclared,
    decimal CacheHit,
    decimal CacheMiss,
    decimal Output,
    bool HighSpeed,
    decimal? BatchMultiplier
);
