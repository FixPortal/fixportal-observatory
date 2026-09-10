using System.Globalization;
using System.Text.RegularExpressions;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing;
using AiObservatory.Data.Pricing.Catalogs;
using AiObservatory.Ingest.Sources;
using NodaTime;

namespace AiObservatory.Ingest.Pricing;

public sealed class KimiPricingSource : IPricingSource, IDisposable
{
#pragma warning disable S1075 // These URLs are the fixed trust boundary required by the pricing design.
    private const string IndexUrl = "https://platform.kimi.ai/docs/llms.txt";
    private static readonly Uri K3Uri = new("https://platform.kimi.ai/docs/pricing/chat-k3.md");
    private static readonly Uri K27Uri = new("https://platform.kimi.ai/docs/pricing/chat-k27-code.md");
    private static readonly Uri K26Uri = new("https://platform.kimi.ai/docs/pricing/chat-k26.md");
    private static readonly Uri K25Uri = new("https://platform.kimi.ai/docs/pricing/chat-k25.md");
    private static readonly Uri BatchUri = new("https://platform.kimi.ai/docs/pricing/batch.md");
#pragma warning restore S1075
    private static readonly Regex QuotedCell = new(
        "\"([^\"]*)\"",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1)
    );
    private static readonly string[] PriceColumns =
    [
        "Model",
        "Unit",
        "Input Price (Cache Hit)",
        "Input Price (Cache Miss)",
        "Output Price",
        "Context Window",
    ];
    private readonly IClock _clock;
    private readonly FirstPartyDocumentFetcher _indexFetcher;
    private readonly IReadOnlyList<(Uri Uri, FirstPartyDocumentFetcher Fetcher)> _pageFetchers;
    private PricingSnapshotCandidate? _lastCandidate;

    public KimiPricingSource(IClock clock, IHttpClientFactory httpClientFactory)
    {
        _clock = clock;
        _indexFetcher = Fetcher(httpClientFactory, new Uri(IndexUrl));
        _pageFetchers =
        [
            (K3Uri, Fetcher(httpClientFactory, K3Uri)),
            (K27Uri, Fetcher(httpClientFactory, K27Uri)),
            (K26Uri, Fetcher(httpClientFactory, K26Uri)),
            (K25Uri, Fetcher(httpClientFactory, K25Uri)),
            (BatchUri, Fetcher(httpClientFactory, BatchUri)),
        ];
    }

    internal KimiPricingSource(IClock clock, HttpMessageHandler? handler)
    {
        _clock = clock;
        _indexFetcher = Fetcher(new Uri(IndexUrl), handler);
        _pageFetchers =
        [
            (K3Uri, Fetcher(K3Uri, handler)),
            (K27Uri, Fetcher(K27Uri, handler)),
            (K26Uri, Fetcher(K26Uri, handler)),
            (K25Uri, Fetcher(K25Uri, handler)),
            (BatchUri, Fetcher(BatchUri, handler)),
        ];
    }

    public string SourceId => PricingSourceIds.Kimi;

    public void Dispose()
    {
        _indexFetcher.Dispose();
        foreach (var (_, fetcher) in _pageFetchers)
        {
            fetcher.Dispose();
        }
    }

    public async Task<PricingSnapshotCandidate?> FetchAsync(CancellationToken cancellationToken)
    {
        var index = await _indexFetcher.FetchAsync(cancellationToken);

        ValidateIndex(index.Content);
        var pages = new List<(Uri Uri, string Content)> { (new Uri(IndexUrl), index.Content) };
        foreach (var (uri, fetcher) in _pageFetchers)
        {
            var page = await fetcher.FetchAsync(cancellationToken);
            pages.Add((uri, page.Content));
        }

        var retrievedAt = _clock.GetCurrentInstant();
        var rawEvidence = string.Join("\n\u001e\n", pages.Select(page => $"{page.Uri.AbsoluteUri}\n{page.Content}"));
        var candidate = PricingCandidate.Create(
            Provider.Moonshot,
            SourceId,
            retrievedAt,
            IndexUrl,
            rawEvidence,
            Parse(pages[1].Content, pages[2].Content, pages[3].Content, pages[4].Content, pages[5].Content, retrievedAt)
        );
        if (_lastCandidate?.ContentHash == candidate.ContentHash)
        {
            return _lastCandidate;
        }

        return _lastCandidate = candidate;
    }

    public static KimiPriceCatalog Parse(
        string k3,
        string k27,
        string k26,
        string k25,
        string batch,
        Instant retrievedAt
    )
    {
        var observedOn = retrievedAt.InUtc().Date;
        var rows = new List<KimiRow>();
        rows.AddRange(ParsePage(k3, "# Flagship Model Kimi K3 Pricing", ["kimi-k3"]));
        rows.AddRange(
            ParsePage(k27, "# Coding Model Kimi K2.7 Code Pricing", ["kimi-k2.7-code", "kimi-k2.7-code-highspeed"])
        );
        rows.AddRange(ParsePage(k26, "# Kimi K2.6 Model Pricing", ["kimi-k2.6"]));
        rows.AddRange(ParsePage(k25, "# Multi-modal Model Kimi K2.5 Pricing", ["kimi-k2.5"]));

        var entries = new Dictionary<string, KimiPriceEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var highSpeed = row.Model.EndsWith("-highspeed", StringComparison.Ordinal);
            if (
                !entries.TryAdd(
                    row.Model,
                    new KimiPriceEntry(
                        row.Model,
                        [row.Model],
                        observedOn,
                        false,
                        row.CacheHit,
                        row.CacheMiss,
                        row.Output,
                        highSpeed,
                        null
                    )
                )
            )
            {
                throw new InvalidDataException("Kimi pricing contains a duplicate or overlapping model.");
            }
        }

        ApplyBatch(entries, batch);
        if (entries.Count != 5)
        {
            throw new InvalidDataException("Kimi pricing must contain exactly five model variants.");
        }

        var catalog = new KimiPriceCatalog(
            "USD",
            IndexUrl,
            retrievedAt,
            entries
                .Values.OrderByDescending(entry => entry.ModelPrefix.Length)
                .ThenBy(entry => entry.ModelPrefix)
                .ToList()
        );
        catalog.Validate();
        return catalog;
    }

    private static void ApplyBatch(Dictionary<string, KimiPriceEntry> entries, string document)
    {
        if (
            !document.Contains(
                "Batch API inference costs are **60%** of the standard model price",
                StringComparison.Ordinal
            )
        )
        {
            throw new InvalidDataException("Kimi Batch multiplier changed or is missing.");
        }

        var rows = ParsePage(
            document,
            "# BatchJob Pricing",
            ["kimi-k2.7-code (Batch)", "kimi-k2.6 (Batch)", "kimi-k2.5 (Batch)"]
        );
        var eligible = new HashSet<string>(
            ["kimi-k2.7-code", "kimi-k2.6", "kimi-k2.5"],
            StringComparer.OrdinalIgnoreCase
        );
        foreach (var row in rows)
        {
            var model = row.Model[..^" (Batch)".Length];
            if (!eligible.Remove(model) || !entries.TryGetValue(model, out var entry))
            {
                throw new InvalidDataException("Kimi Batch pricing contains an unknown or duplicate model.");
            }

            if (
                !MatchesRounded(entry.CacheHit * 0.6m, row.CacheHit)
                || !MatchesRounded(entry.CacheMiss * 0.6m, row.CacheMiss)
                || !MatchesRounded(entry.Output * 0.6m, row.Output)
            )
            {
                throw new InvalidDataException("Kimi Batch rates do not match the declared 60% multiplier.");
            }

            entries[model] = entry with { BatchMultiplier = 0.6m };
        }

        if (eligible.Count != 0)
        {
            throw new InvalidDataException("Kimi Batch pricing is partial.");
        }
    }

    private static bool MatchesRounded(decimal expected, decimal published)
    {
        var scale = (decimal.GetBits(published)[3] >> 16) & 0x7F;
        return Math.Round(expected, scale, MidpointRounding.AwayFromZero) == published;
    }

    private static IReadOnlyList<KimiRow> ParsePage(
        string document,
        string title,
        IReadOnlyCollection<string> expectedModels
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(document);
        var lines = Lines(document);
        _ = SingleLine(lines, title);
        _ = SingleLine(lines, "## Product Pricing");
        var columnsStart = SingleLine(lines, "columns={[");
        var columns = new List<string>();
        var index = columnsStart + 1;
        for (; index < lines.Length && lines[index].Trim() != "]}"; index++)
        {
            var line = lines[index].Trim();
            const string marker = "{ title: \"";
            if (!line.StartsWith(marker, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Kimi pricing column declaration changed.");
            }

            var end = line.IndexOf('"', marker.Length);
            if (end < 0)
            {
                throw new InvalidDataException("Kimi pricing column declaration is partial.");
            }

            columns.Add(line[marker.Length..end]);
        }

        if (index == lines.Length || !columns.SequenceEqual(PriceColumns, StringComparer.Ordinal))
        {
            throw new InvalidDataException("Kimi pricing columns changed.");
        }

        var rowsStart = SingleLine(lines, "rows={[");
        var rows = new List<KimiRow>();
        for (index = rowsStart + 1; index < lines.Length && lines[index].Trim() != "]}"; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            rows.Add(ParseRow(line));
        }

        if (
            index == lines.Length
            || rows.Count != expectedModels.Count
            || !rows.Select(row => row.Model).ToHashSet(StringComparer.Ordinal).SetEquals(expectedModels)
        )
        {
            throw new InvalidDataException("Kimi pricing model rows are partial, duplicate, or changed.");
        }

        return rows;
    }

    private static KimiRow ParseRow(string line)
    {
        var normalized = line.Replace("<>{\"$\"}", "\"$", StringComparison.Ordinal)
            .Replace("</>", "\"", StringComparison.Ordinal);
        var matches = QuotedCell.Matches(normalized);
        // The final row of a page may render without the trailing comma the literal rows carry.
        if (
            matches.Count != 6
            || QuotedCell.Replace(normalized, "\"\"").TrimEnd(',') != "[\"\", \"\", \"\", \"\", \"\", \"\"]"
        )
        {
            throw new InvalidDataException("Kimi pricing row shape changed.");
        }

        var cells = matches.Select(match => match.Groups[1].Value).ToArray();
        if (cells[1] != "1M tokens" || !cells[5].EndsWith(" tokens", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Kimi pricing unit or context shape changed.");
        }

        return new KimiRow(cells[0], ParseRate(cells[2]), ParseRate(cells[3]), ParseRate(cells[4]));
    }

    private static decimal ParseRate(string value)
    {
        if (
            !value.StartsWith('$')
            || !decimal.TryParse(
                value.AsSpan(1),
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var rate
            )
            || rate <= 0
        )
        {
            throw new InvalidDataException("Kimi pricing contains a non-USD or non-positive rate.");
        }

        return rate;
    }

    private static void ValidateIndex(string index)
    {
        if (new[] { K3Uri, K27Uri, K26Uri, K25Uri, BatchUri }.Any(uri => Count(index, uri.AbsoluteUri) != 1))
        {
            throw new InvalidDataException("The Kimi documentation index is partial or ambiguous.");
        }
    }

    private static int Count(string value, string needle)
    {
        var count = 0;
        var start = 0;
        while ((start = value.IndexOf(needle, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += needle.Length;
        }

        return count;
    }

    private static int SingleLine(IReadOnlyList<string> lines, string expected)
    {
        var matches = Enumerable.Range(0, lines.Count).Where(index => lines[index].Trim() == expected).ToList();
        return matches.Count == 1
            ? matches[0]
            : throw new InvalidDataException($"Kimi pricing requires exactly one '{expected}' line.");
    }

    private static FirstPartyDocumentFetcher Fetcher(Uri uri, HttpMessageHandler? handler) =>
        new(uri, ["platform.kimi.ai"], handler);

    private static FirstPartyDocumentFetcher Fetcher(IHttpClientFactory httpClientFactory, Uri uri) =>
        new(httpClientFactory.CreateClient(FirstPartyDocumentFetcher.HttpClientName), uri, ["platform.kimi.ai"]);

    private static string[] Lines(string document) =>
        document.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    private sealed record KimiRow(string Model, decimal CacheHit, decimal CacheMiss, decimal Output);
}
