using NodaTime;

namespace AiObservatory.Data.Pricing.Catalogs;

public sealed record AnthropicPriceCatalog(
    string Currency,
    string SourceUrl,
    Instant RetrievedAt,
    IReadOnlyList<AnthropicPriceEntry> Entries
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
            throw new InvalidDataException("Anthropic pricing must be USD and have an HTTPS source URL.");
        }

        if (Entries is null || Entries.Count == 0)
        {
            throw new InvalidDataException("Anthropic pricing must contain entries.");
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
                || entry.Input <= 0
                || entry.Output <= 0
                || entry.CacheRead <= 0
                || entry.CacheWrite5m <= 0
                || entry.CacheWrite1h <= 0
                || !PositivePair(entry.BatchInput, entry.BatchOutput)
                || !PositivePair(entry.FastInput, entry.FastOutput)
                || entry.UsInferenceMultiplier is <= 0
            )
            {
                throw new InvalidDataException("Anthropic pricing contains an incomplete or non-positive entry.");
            }

            foreach (var alias in entry.Aliases.Prepend(entry.ModelPrefix).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (effectiveDates.TryGetValue(alias, out var previous) && entry.EffectiveFrom <= previous)
                {
                    throw new InvalidDataException("Anthropic effective windows must be unique and ordered.");
                }

                effectiveDates[alias] = entry.EffectiveFrom;
            }
        }
    }

    public AnthropicPriceEntry? Resolve(string model, LocalDate usageDate)
    {
        return EffectiveWindow
            .ApplicableAt(
                Entries.Where(entry =>
                    entry
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

    private static bool PositivePair(decimal? first, decimal? second) =>
        first is null && second is null || first is > 0 && second is > 0;
}

public sealed record AnthropicPriceEntry(
    string ModelPrefix,
    IReadOnlyList<string> Aliases,
    LocalDate EffectiveFrom,
    bool EffectiveDateIsProviderDeclared,
    decimal Input,
    decimal Output,
    decimal CacheRead,
    decimal CacheWrite5m,
    decimal CacheWrite1h,
    decimal? BatchInput,
    decimal? BatchOutput,
    decimal? FastInput,
    decimal? FastOutput,
    decimal? UsInferenceMultiplier
);
