using NodaTime;

namespace AiObservatory.Data.Pricing.Catalogs;

public sealed record OpenAiPriceCatalog(
    string Currency,
    string SourceUrl,
    Instant RetrievedAt,
    IReadOnlyList<OpenAiPriceEntry> Entries
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
            throw new InvalidDataException("OpenAI pricing must be USD and have an HTTPS source URL.");
        }

        if (Entries is null || Entries.Count == 0)
        {
            throw new InvalidDataException("OpenAI pricing must contain entries.");
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
                || string.IsNullOrWhiteSpace(entry.Processing)
                || string.IsNullOrWhiteSpace(entry.Context)
                || string.IsNullOrWhiteSpace(entry.Region)
                || entry.Input <= 0
                || entry.CachedInput is <= 0
                || entry.CacheWrite is <= 0
                || entry.Output <= 0
            )
            {
                throw new InvalidDataException("OpenAI pricing contains an incomplete or non-positive entry.");
            }

            foreach (var alias in entry.Aliases.Prepend(entry.ModelPrefix).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var key = string.Join('\u001f', alias, entry.Processing, entry.Context, entry.Region);
                if (effectiveDates.TryGetValue(key, out var previous) && entry.EffectiveFrom <= previous)
                {
                    throw new InvalidDataException("OpenAI effective windows must be unique and ordered.");
                }

                effectiveDates[key] = entry.EffectiveFrom;
            }
        }
    }

    public OpenAiPriceEntry? Resolve(
        string model,
        string processing,
        string context,
        string region,
        LocalDate usageDate
    )
    {
        return EffectiveWindow
            .ApplicableAt(
                Entries.Where(entry =>
                    string.Equals(entry.Processing, processing, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(entry.Context, context, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(entry.Region, region, StringComparison.OrdinalIgnoreCase)
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

public sealed record OpenAiPriceEntry(
    string ModelPrefix,
    IReadOnlyList<string> Aliases,
    LocalDate EffectiveFrom,
    bool EffectiveDateIsProviderDeclared,
    string Processing,
    string Context,
    string Region,
    decimal Input,
    decimal? CachedInput,
    decimal Output,
    decimal? CacheWrite
);
