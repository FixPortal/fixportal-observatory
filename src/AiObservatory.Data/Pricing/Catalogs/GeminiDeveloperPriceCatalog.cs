using NodaTime;

namespace AiObservatory.Data.Pricing.Catalogs;

public sealed record GeminiDeveloperPriceCatalog(
    string Currency,
    string SourceUrl,
    Instant RetrievedAt,
    IReadOnlyList<GeminiDeveloperPriceEntry> Entries
)
{
    public void Validate()
    {
        if (
            Currency != "USD"
            || !Uri.TryCreate(SourceUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || Entries is null
            || Entries.Count == 0
        )
        {
            throw new InvalidDataException(
                "Gemini Developer API pricing must be USD, sourced over HTTPS, and contain entries."
            );
        }

        var windows = new Dictionary<string, LocalDate>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Entries)
        {
            if (
                string.IsNullOrWhiteSpace(entry.ModelPrefix)
                || entry.Aliases is null
                || entry.Aliases.Count == 0
                || entry.Aliases.Any(string.IsNullOrWhiteSpace)
                || entry.Aliases.Distinct(StringComparer.OrdinalIgnoreCase).Count() != entry.Aliases.Count
                || string.IsNullOrWhiteSpace(entry.Tier)
                || entry.Context is not ("short" or "long")
                || entry.Input <= 0
                || entry.CachedInput <= 0
                || entry.CachedInput > entry.Input
                || entry.Output <= 0
            )
            {
                throw new InvalidDataException(
                    "Gemini Developer API pricing contains an incomplete or non-positive entry."
                );
            }

            foreach (var alias in entry.Aliases.Prepend(entry.ModelPrefix).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var key = string.Join('\u001f', alias, entry.Tier, entry.Context);
                if (windows.TryGetValue(key, out var previous) && entry.EffectiveFrom <= previous)
                {
                    throw new InvalidDataException(
                        "Gemini Developer API effective windows must be unique and ordered."
                    );
                }

                windows[key] = entry.EffectiveFrom;
            }
        }
    }

    public GeminiDeveloperPriceEntry? Resolve(string model, string tier, string context, LocalDate usageDate) =>
        EffectiveWindow
            .ApplicableAt(
                Entries.Where(entry =>
                    string.Equals(entry.Tier, tier, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(entry.Context, context, StringComparison.OrdinalIgnoreCase)
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

public sealed record GeminiDeveloperPriceEntry(
    string ModelPrefix,
    IReadOnlyList<string> Aliases,
    LocalDate EffectiveFrom,
    bool EffectiveDateIsProviderDeclared,
    string Tier,
    string Context,
    decimal Input,
    decimal CachedInput,
    decimal Output
);
