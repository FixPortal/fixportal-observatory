using System.Globalization;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing;
using AiObservatory.Data.Pricing.Catalogs;
using AiObservatory.Ingest.Sources;
using NodaTime;

namespace AiObservatory.Ingest.Pricing;

public sealed class ClaudePricingSource : IPricingSource, IDisposable
{
#pragma warning disable S1075 // The source URL is the fixed trust boundary required by the pricing design.
    private const string SourceUrl = "https://platform.claude.com/docs/en/about-claude/pricing.md";
#pragma warning restore S1075
    private static readonly string[] ModelColumns =
    [
        "Model",
        "Base Input Tokens",
        "5m Cache Writes",
        "1h Cache Writes",
        "Cache Hits & Refreshes",
        "Output Tokens",
    ];
    private static readonly string[] CacheColumns = ["Cache operation", "Multiplier", "Duration"];
    private static readonly string[] FastColumns = ["Model", "Input", "Output"];
    private static readonly string[] BatchColumns = ["Model", "Batch input", "Batch output"];
    private static readonly string[] RequiredModels =
    [
        "claude-opus-5",
        "claude-opus-4-8",
        "claude-opus-4-5",
        "claude-sonnet-5",
    ];
    private static readonly string[] RequiredFastModels = ["claude-opus-5", "claude-opus-4-8"];
    private readonly IClock _clock;
    private readonly FirstPartyDocumentFetcher _fetcher;
    private PricingSnapshotCandidate? _lastCandidate;

    public ClaudePricingSource(IClock clock, IHttpClientFactory httpClientFactory)
    {
        _clock = clock;
        _fetcher = new FirstPartyDocumentFetcher(
            httpClientFactory.CreateClient(FirstPartyDocumentFetcher.HttpClientName),
            new Uri(SourceUrl),
            ["platform.claude.com"]
        );
    }

    internal ClaudePricingSource(IClock clock, HttpMessageHandler? handler)
    {
        _clock = clock;
        _fetcher = new FirstPartyDocumentFetcher(new Uri(SourceUrl), ["platform.claude.com"], handler);
    }

    public string SourceId => PricingSourceIds.Claude;

    public void Dispose() => _fetcher.Dispose();

    public async Task<PricingSnapshotCandidate?> FetchAsync(CancellationToken cancellationToken)
    {
        var document = await _fetcher.FetchAsync(cancellationToken);

        var retrievedAt = _clock.GetCurrentInstant();
        var raw = document.Content;
        var candidate = PricingCandidate.Create(
            Provider.Anthropic,
            SourceId,
            retrievedAt,
            SourceUrl,
            raw,
            Parse(raw, retrievedAt)
        );
        if (_lastCandidate?.ContentHash == candidate.ContentHash)
        {
            return _lastCandidate;
        }

        return _lastCandidate = candidate;
    }

    public static AnthropicPriceCatalog Parse(string document, Instant retrievedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(document);
        if (!document.Contains("All prices are in USD.", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Claude pricing is not explicitly USD.");
        }

        var lines = Lines(document);
        var observedOn = retrievedAt.InUtc().Date;
        var baseRows = TableRows(lines, "## Model pricing", ModelColumns);
        var entries = new Dictionary<string, AnthropicPriceEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in baseRows)
        {
            var model = NormalizeModel(row[0]);
            var entry = new AnthropicPriceEntry(
                model,
                [model],
                observedOn,
                false,
                ParseRate(row[1]),
                ParseRate(row[5]),
                ParseRate(row[4]),
                ParseRate(row[2]),
                ParseRate(row[3]),
                null,
                null,
                null,
                null,
                SupportsUsInference(model) ? 1.1m : null
            );
            if (!entries.TryAdd(model, entry))
            {
                throw new InvalidDataException("Claude pricing contains a duplicate or overlapping model.");
            }
        }

        ValidateCacheMultipliers(TableRows(lines, "### Prompt caching", CacheColumns));
        ValidateGeography(document);
        ApplyFastRates(entries, TableRows(lines, "### Fast mode pricing", FastColumns));
        ApplyBatchRates(entries, TableRows(lines, "### Batch processing", BatchColumns));
        if (
            RequiredModels.Any(model =>
                !entries.TryGetValue(model, out var entry) || entry.BatchInput is null || entry.BatchOutput is null
            )
            || RequiredFastModels.Any(model =>
                !entries.TryGetValue(model, out var entry) || entry.FastInput is null || entry.FastOutput is null
            )
        )
        {
            throw new InvalidDataException("Claude pricing is missing required current model coverage.");
        }

        var catalog = new AnthropicPriceCatalog(
            "USD",
            SourceUrl,
            retrievedAt,
            entries
                .Values.OrderByDescending(entry => entry.ModelPrefix.Length)
                .ThenBy(entry => entry.ModelPrefix)
                .ToList()
        );
        catalog.Validate();
        return catalog;
    }

    private static void ApplyBatchRates(Dictionary<string, AnthropicPriceEntry> entries, IReadOnlyList<string[]> rows)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var model = NormalizeModel(row[0]);
            if (!seen.Add(model) || !entries.TryGetValue(model, out var entry))
            {
                throw new InvalidDataException("Claude Batch pricing contains an unknown or duplicate model.");
            }

            entries[model] = entry with { BatchInput = ParseRate(row[1]), BatchOutput = ParseRate(row[2]) };
        }

        if (!seen.SetEquals(entries.Keys))
        {
            throw new InvalidDataException("Claude Batch pricing is partial.");
        }
    }

    private static void ApplyFastRates(Dictionary<string, AnthropicPriceEntry> entries, IReadOnlyList<string[]> rows)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            foreach (var displayName in row[0].Split(" / ", StringSplitOptions.RemoveEmptyEntries))
            {
                var model = NormalizeModel(displayName);
                if (!seen.Add(model) || !entries.TryGetValue(model, out var entry))
                {
                    throw new InvalidDataException("Claude fast pricing contains an unknown or duplicate model.");
                }

                entries[model] = entry with { FastInput = ParseRate(row[1]), FastOutput = ParseRate(row[2]) };
            }
        }
    }

    private static void ValidateCacheMultipliers(IReadOnlyList<string[]> rows)
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["5-minute cache write"] = "1.25x base input price",
            ["1-hour cache write"] = "2x base input price",
            ["Cache read (hit)"] = "0.1x base input price",
        };
        if (
            rows.Count != expected.Count
            || rows.Any(row =>
                !expected.TryGetValue(row[0], out var multiplier) || multiplier != HeadlineMultiplier(row[1])
            )
        )
        {
            throw new InvalidDataException("Claude prompt-cache pricing shape changed.");
        }
    }

    // Unlike the header and separator tolerances, this one absorbs a REAL pricing change, so
    // it is deliberately narrow. On 2026-09-19 the cache-read row reads "0.1x base input
    // price (0.025x on Claude Fable 5.1 and Claude Mythos 5.1)" -- a per-model exception, not
    // a restyle. It does not change what this parser stores: every per-model cache-read rate
    // is taken from the explicit column of the model table above, and this table is only a
    // cross-check that the headline multiplier still holds. Comparing the headline and
    // allowing a trailing parenthesised qualifier keeps that cross-check meaningful, because
    // a change to the headline multiplier itself -- 0.1x becoming 0.15x -- still throws.
    private static string HeadlineMultiplier(string value)
    {
        var qualifier = value.IndexOf(" (", StringComparison.Ordinal);
        return (qualifier < 0 ? value : value[..qualifier]).Trim();
    }

    private static void ValidateGeography(string document)
    {
        if (
            !document.Contains("### Data residency pricing", StringComparison.Ordinal)
            || !document.Contains("For Claude 4.6 and later models", StringComparison.Ordinal)
            || !document.Contains("incurs a 1.1x multiplier", StringComparison.Ordinal)
        )
        {
            throw new InvalidDataException("Claude inference-geography pricing shape changed.");
        }
    }

    private static bool SupportsUsInference(string model)
    {
        var parts = model.Split('-');
        if (parts.Length is < 3 or > 4 || !int.TryParse(parts[2], CultureInfo.InvariantCulture, out var major))
        {
            return false;
        }

        var minor =
            parts.Length == 4 && int.TryParse(parts[3], CultureInfo.InvariantCulture, out var parsedMinor)
                ? parsedMinor
                : 0;
        return major > 4 || major == 4 && minor >= 6;
    }

    // A trailing footnote marker is presentation as well: the live table renders a rate that
    // carries a caveat below the table as "$0.25 / MTok<sup>1</sup>". Strip one marker so the
    // rate parses; anything else trailing the unit still fails.
    private static string StripTrailingFootnote(string value)
    {
        const string close = "</sup>";
        if (!value.EndsWith(close, StringComparison.Ordinal))
        {
            return value;
        }

        var open = value.LastIndexOf("<sup>", StringComparison.Ordinal);
        return open < 0 ? value : value[..open].TrimEnd();
    }

    private static decimal ParseRate(string rawValue)
    {
        const string suffix = " / MTok";
        var value = StripTrailingFootnote(rawValue);
        if (
            !value.StartsWith('$')
            || !value.EndsWith(suffix, StringComparison.Ordinal)
            || !decimal.TryParse(
                value.AsSpan(1, value.Length - suffix.Length - 1),
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var rate
            )
            || rate <= 0
        )
        {
            throw new InvalidDataException("Claude pricing contains a non-USD or non-positive rate.");
        }

        return rate;
    }

    private static string NormalizeModel(string value)
    {
        var suffix = value.IndexOf(" (", StringComparison.Ordinal);
        var displayName = (suffix < 0 ? value : value[..suffix]).Trim();
        if (!displayName.StartsWith("Claude ", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Claude pricing contains an unknown model name.");
        }

        return displayName.ToLowerInvariant().Replace('.', '-').Replace(' ', '-');
    }

    private static IReadOnlyList<string[]> TableRows(
        IReadOnlyList<string> lines,
        string heading,
        IReadOnlyList<string> expectedColumns
    )
    {
        var headingIndex = SingleLine(lines, heading);
        var headerIndex = -1;
        for (var index = headingIndex + 1; index < lines.Count; index++)
        {
            var trimmed = lines[index].Trim();
            if (trimmed.StartsWith('#'))
            {
                break;
            }

            if (trimmed.StartsWith('|'))
            {
                headerIndex = index;
                break;
            }
        }

        if (
            headerIndex < 0
            || !Cells(lines[headerIndex])
                .Select(NormalizeColumn)
                .SequenceEqual(expectedColumns.Select(NormalizeColumn), StringComparer.Ordinal)
        )
        {
            throw new InvalidDataException($"Claude '{heading}' pricing columns changed.");
        }

        var separatorIndex = NextContentLine(lines, headerIndex + 1);
        ValidateSeparator(lines[separatorIndex], expectedColumns.Count);
        var rows = new List<string[]>();
        for (var index = separatorIndex + 1; index < lines.Count && lines[index].TrimStart().StartsWith('|'); index++)
        {
            var cells = Cells(lines[index]);
            if (cells.Length != expectedColumns.Count)
            {
                throw new InvalidDataException($"Claude '{heading}' pricing row is partial.");
            }

            rows.Add(cells);
        }

        return rows.Count > 0 ? rows : throw new InvalidDataException($"Claude '{heading}' pricing table is empty.");
    }

    private static int SingleLine(IReadOnlyList<string> lines, string expected)
    {
        var matches = Enumerable.Range(0, lines.Count).Where(index => lines[index].Trim() == expected).ToList();
        return matches.Count == 1
            ? matches[0]
            : throw new InvalidDataException($"Claude pricing requires exactly one '{expected}' heading.");
    }

    private static int NextContentLine(IReadOnlyList<string> lines, int start)
    {
        for (var index = start; index < lines.Count; index++)
        {
            if (!string.IsNullOrWhiteSpace(lines[index]))
            {
                return index;
            }
        }

        throw new InvalidDataException("Claude pricing ended before its required table.");
    }

    private static string[] Cells(string line)
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith('|') || !trimmed.EndsWith('|'))
        {
            throw new InvalidDataException("Claude pricing table shape changed.");
        }

        return trimmed.Split('|')[1..^1].Select(cell => cell.Trim()).ToArray();
    }

    // A column heading is presentation, not structure. Anthropic restyled these on
    // 2026-09-01 -- Title Case became sentence case and "&" was spelled "and" -- which
    // changed no column, no order and no rate, yet hard-failed the parser 56 times in a row
    // while the dashboard kept serving the last good catalog. Comparing the normalized form
    // keeps the real contract, which is the SET and ORDER of columns: a renamed, dropped or
    // reordered column still throws.
    private static string NormalizeColumn(string value) =>
        string.Join(
                ' ',
                value
                    .Replace("&", "and", StringComparison.Ordinal)
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            )
            .ToLowerInvariant();

    private static void ValidateSeparator(string line, int columns)
    {
        var cells = Cells(line);
        if (cells.Length != columns || cells.Any(cell => !IsSeparatorCell(cell)))
        {
            throw new InvalidDataException("Claude pricing table separator changed.");
        }
    }

    // Markdown alignment colons are presentation for the same reason: the same 2026-09-01
    // edit gave two of the four tables ":---" separators. The structural requirement is a
    // dash run, optionally alignment-marked at either end.
    private static bool IsSeparatorCell(string cell)
    {
        var span = cell.AsSpan();
        if (span.Length > 0 && span[0] == ':')
        {
            span = span[1..];
        }

        if (span.Length > 0 && span[^1] == ':')
        {
            span = span[..^1];
        }

        return span.Length >= 3 && span.IndexOfAnyExcept('-') < 0;
    }

    private static string[] Lines(string document) =>
        document.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
}
