using NodaTime;

namespace AiObservatory.Data.Pricing.Catalogs;

/// <summary>
/// xAI's text API price list. Every row on the published table appears twice — once for
/// requests whose prompt stays under the threshold and once for requests that reach it —
/// and the footnote is explicit that reaching the threshold bills <em>every</em> token in
/// that request at the higher rate. Both lanes therefore live on one entry keyed by model,
/// and the caller states which lane the request took; splitting them into separate entries
/// would make the pair look like two effective-date windows for the same model.
/// </summary>
public sealed record XaiPriceCatalog(
    string Currency,
    string SourceUrl,
    Instant RetrievedAt,
    long LongContextThresholdTokens,
    IReadOnlyList<XaiPriceEntry> Entries
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
            throw new InvalidDataException("xAI pricing must be USD and have an HTTPS source URL.");
        }

        if (LongContextThresholdTokens <= 0)
        {
            throw new InvalidDataException("xAI pricing must declare a positive long-context threshold.");
        }

        if (Entries is null || Entries.Count == 0)
        {
            throw new InvalidDataException("xAI pricing must contain entries.");
        }

        var effectiveDates = new Dictionary<string, LocalDate>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Entries)
        {
            ValidateEntry(entry);
            foreach (var name in entry.Names())
            {
                if (effectiveDates.TryGetValue(name, out var previous) && entry.EffectiveFrom <= previous)
                {
                    throw new InvalidDataException("xAI effective windows must be unique and ordered.");
                }

                effectiveDates[name] = entry.EffectiveFrom;
            }
        }
    }

    private static void ValidateEntry(XaiPriceEntry entry)
    {
        if (
            string.IsNullOrWhiteSpace(entry.Model)
            || entry.Aliases is null
            || entry.Aliases.Any(string.IsNullOrWhiteSpace)
            || entry.Aliases.Distinct(StringComparer.OrdinalIgnoreCase).Count() != entry.Aliases.Count
            || HasNonPositiveRate(entry)
        )
        {
            throw new InvalidDataException("xAI pricing contains an incomplete or non-positive entry.");
        }

        // The published table has never priced a long-context request below its standard lane.
        // An inversion means the two lanes were read off the wrong rows, which would silently
        // under-bill every long request, so refuse rather than store it.
        if (
            entry.LongContextInput < entry.Input
            || entry.LongContextCachedInput < entry.CachedInput
            || entry.LongContextOutput < entry.Output
        )
        {
            throw new InvalidDataException("xAI long-context rates must not undercut the standard lane.");
        }

        if (entry.CachedInput > entry.Input || entry.LongContextCachedInput > entry.LongContextInput)
        {
            throw new InvalidDataException("xAI cached input must not cost more than uncached input.");
        }
    }

    private static bool HasNonPositiveRate(XaiPriceEntry entry) =>
        entry.Input <= 0
        || entry.CachedInput <= 0
        || entry.Output <= 0
        || entry.LongContextInput <= 0
        || entry.LongContextCachedInput <= 0
        || entry.LongContextOutput <= 0;

    /// <summary>
    /// Resolves on an exact model id or a declared alias, never on a prefix. `grok-4.6` is a
    /// prefix of `grok-4.6-build`, a CLI-only model absent from the published table, so prefix
    /// matching would quietly price it at 4.6's rates. An unmatched model returns null and the
    /// event stays unpriced, which is the visible outcome rather than the invented one.
    /// </summary>
    public XaiPriceEntry? Resolve(string model, LocalDate usageDate)
    {
        return EffectiveWindow
            .ApplicableAt(
                Entries.Where(entry => entry.Names().Contains(model, StringComparer.OrdinalIgnoreCase)),
                usageDate,
                entry => entry.EffectiveFrom,
                entry => entry.EffectiveDateIsProviderDeclared
            )
            .OrderByDescending(entry => entry.EffectiveFrom)
            .FirstOrDefault();
    }
}

public sealed record XaiPriceEntry(
    string Model,
    IReadOnlyList<string> Aliases,
    LocalDate EffectiveFrom,
    bool EffectiveDateIsProviderDeclared,
    decimal Input,
    decimal CachedInput,
    decimal Output,
    decimal LongContextInput,
    decimal LongContextCachedInput,
    decimal LongContextOutput
)
{
    public IEnumerable<string> Names() => Aliases.Prepend(Model).Distinct(StringComparer.OrdinalIgnoreCase);
}
