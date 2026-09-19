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

    // Moonshot consolidated four per-model pages -- chat-k3.md, chat-k27-code.md,
    // chat-k26.md and chat-k25.md -- into a single chat.md on or before 2026-08-30. The old
    // URLs return nothing and are absent from llms.txt, so ValidateIndex threw on every pass
    // for 61 consecutive days while the dashboard served the last good catalog.
    private static readonly Uri ChatUri = new("https://platform.kimi.ai/docs/pricing/chat.md");
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

    // The catalog used to pin an exact five-variant model set, which meant Moonshot retiring
    // kimi-k2.5 would have broken ingest even after the URL consolidation was handled. The
    // structural guarantees below -- columns, units, row shape, the batch multiplier -- are
    // what make this source an authority; the exact roster is not, and pinning it turns any
    // upstream product decision into an outage. Require only the models actually relied on,
    // and let the roster move. (kimi-k2.5 is retired upstream as of 2026-09-19; no usage of
    // it was ever recorded in this Observatory.)
    private static readonly string[] RequiredModels = ["kimi-k3", "kimi-k2.7-code"];
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
            (ChatUri, Fetcher(httpClientFactory, ChatUri)),
            (BatchUri, Fetcher(httpClientFactory, BatchUri)),
        ];
    }

    internal KimiPricingSource(IClock clock, HttpMessageHandler? handler)
    {
        _clock = clock;
        _indexFetcher = Fetcher(new Uri(IndexUrl), handler);
        _pageFetchers = [(ChatUri, Fetcher(ChatUri, handler)), (BatchUri, Fetcher(BatchUri, handler))];
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
            Parse(pages[1].Content, pages[2].Content, retrievedAt)
        );
        if (_lastCandidate?.ContentHash == candidate.ContentHash)
        {
            return _lastCandidate;
        }

        return _lastCandidate = candidate;
    }

    public static KimiPriceCatalog Parse(string chat, string batch, Instant retrievedAt)
    {
        var observedOn = retrievedAt.InUtc().Date;
        var rows = ParsePage(chat, "# Model Inference Pricing Explanation", "## Model Pricing").ToList();

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
        if (RequiredModels.Any(model => !entries.ContainsKey(model)))
        {
            throw new InvalidDataException("Kimi pricing is missing a required model.");
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

        const string batchSuffix = " (Batch)";
        var rows = ParsePage(document, "# BatchJob Pricing", "## Product Pricing");

        // Which models Moonshot offers on Batch is their product decision, so it is derived
        // from the page rather than pinned here. What still has to hold is that every batch
        // row names a model this catalog already priced, names it once, and matches the
        // declared 60% multiplier -- that is the property worth failing over.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (!row.Model.EndsWith(batchSuffix, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Kimi Batch pricing row is not a Batch variant.");
            }

            var model = row.Model[..^batchSuffix.Length];
            if (!seen.Add(model) || !entries.TryGetValue(model, out var entry))
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

        if (seen.Count == 0)
        {
            throw new InvalidDataException("Kimi Batch pricing is partial.");
        }
    }

    private static bool MatchesRounded(decimal expected, decimal published)
    {
        var scale = (decimal.GetBits(published)[3] >> 16) & 0x7F;
        return Math.Round(expected, scale, MidpointRounding.AwayFromZero) == published;
    }

    private static IReadOnlyList<KimiRow> ParsePage(string document, string title, string sectionHeading)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(document);
        var lines = Lines(document);
        _ = SingleLine(lines, title);
        _ = SingleLine(lines, sectionHeading);
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
            || rows.Count == 0
            || rows.Select(row => row.Model).ToHashSet(StringComparer.Ordinal).Count != rows.Count
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
        if (new[] { ChatUri, BatchUri }.Any(uri => Count(index, uri.AbsoluteUri) != 1))
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
