using System.Globalization;
using System.Text.Json.Serialization;
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

        var effectiveDates = new Dictionary<(string Alias, string? Context), LocalDate>();
        var windows = new Dictionary<(string Alias, LocalDate Date), List<AnthropicPriceEntry>>();
        foreach (var entry in Entries)
        {
            ValidateEntry(entry);

            foreach (var alias in entry.Aliases.Prepend(entry.ModelPrefix).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var lane = (alias.ToLowerInvariant(), entry.Context);
                if (effectiveDates.TryGetValue(lane, out var previous) && entry.EffectiveFrom <= previous)
                {
                    throw new InvalidDataException("Anthropic effective windows must be unique and ordered.");
                }

                effectiveDates[lane] = entry.EffectiveFrom;
                var window = (lane.Item1, entry.EffectiveFrom);
                if (!windows.TryGetValue(window, out var entries))
                {
                    entries = [];
                    windows.Add(window, entries);
                }
                entries.Add(entry);
            }
        }
        foreach (var entries in windows.Values)
        {
            ValidateWindow(entries);
        }
    }

    private static void ValidateEntry(AnthropicPriceEntry entry)
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
            || entry.Context is not null
                && (
                    !entry.ModelPrefix.Equals("claude-haiku-5-5", StringComparison.OrdinalIgnoreCase)
                    || entry.Context is not ("prompt_le_100000" or "prompt_gt_100000")
                )
        )
        {
            throw new InvalidDataException("Anthropic pricing contains an incomplete or non-positive entry.");
        }
    }

    private static void ValidateWindow(List<AnthropicPriceEntry> entries)
    {
        if (entries.Count == 1 && entries[0].Context is null)
        {
            return;
        }
        if (
            entries.Count != 2
            || !entries
                .Select(entry => entry.Context)
                .ToHashSet(StringComparer.Ordinal)
                .SetEquals(["prompt_le_100000", "prompt_gt_100000"])
            || entries[0].EffectiveDateIsProviderDeclared != entries[1].EffectiveDateIsProviderDeclared
            || !entries[0].ModelPrefix.Equals(entries[1].ModelPrefix, StringComparison.OrdinalIgnoreCase)
        )
        {
            throw new InvalidDataException("Anthropic prompt pricing requires exactly two bands in the same window.");
        }
    }

    public AnthropicPriceEntry? Resolve(string model, LocalDate usageDate, string? context = null)
    {
        var era = ApplicableEra(model, usageDate);
        return era.FirstOrDefault(entry => entry.Context is null || entry.Context == context);
    }

    public bool RequiresPromptContext(string model, LocalDate usageDate) =>
        ApplicableEra(model, usageDate).Any(entry => entry.Context is not null);

    private IReadOnlyList<AnthropicPriceEntry> ApplicableEra(string model, LocalDate usageDate)
    {
        var matches = Entries
            .Select(entry => new
            {
                Entry = entry,
                Specificity = entry
                    .Aliases.Prepend(entry.ModelPrefix)
                    .Where(alias => MatchesModel(model, alias))
                    .Select(alias => alias.Length)
                    .DefaultIfEmpty(0)
                    .Max(),
            })
            .Where(match => match.Specificity > 0)
            .ToList();
        var applicable = EffectiveWindow.ApplicableAt(
            matches,
            usageDate,
            match => match.Entry.EffectiveFrom,
            match => match.Entry.EffectiveDateIsProviderDeclared
        );
        if (applicable.Count == 0)
        {
            return [];
        }
        var specificity = applicable.Max(match => match.Specificity);
        var family = applicable.Where(match => match.Specificity == specificity).ToList();
        var date = family.Max(match => match.Entry.EffectiveFrom);
        // Select the applicable model/window before the context, so an absent band cannot
        // fall back to a historical flat entry in this catalog.
        return family.Where(match => match.Entry.EffectiveFrom == date).Select(match => match.Entry).ToList();
    }

    private static bool MatchesModel(string model, string alias) =>
        model.Equals(alias, StringComparison.OrdinalIgnoreCase)
        || model.Length == alias.Length + 9
            && model.StartsWith(alias + "-", StringComparison.OrdinalIgnoreCase)
            && DateOnly.TryParseExact(
                model.AsSpan(alias.Length + 1),
                "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _
            );

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
)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Context { get; init; }
}
