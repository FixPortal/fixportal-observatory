using System.Globalization;
using System.Text.RegularExpressions;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing;
using AiObservatory.Data.Pricing.Catalogs;
using AiObservatory.Ingest.Sources;
using NodaTime;

namespace AiObservatory.Ingest.Pricing;

/// <summary>
/// xAI publishes its text API rates as a markdown table served from its own docs host, the
/// same trust shape the Kimi source consumes. Each model occupies two rows — a standard row
/// and a long-context row distinguished by the prompt-token threshold in the model cell — and
/// a model is only accepted when both of its rows are present, because one row alone cannot
/// say which lane it is.
/// </summary>
public sealed class XaiPricingSource : IPricingSource, IDisposable
{
#pragma warning disable S1075 // This URL is the fixed trust boundary required by the pricing design.
    private const string PricingUrl = "https://docs.x.ai/developers/pricing.md";
#pragma warning restore S1075
    private const string TableHeading = "Text API Pricing";
    private const string ThresholdFootnote =
        "requests whose prompt reaches the listed token threshold are billed at the higher rate";

    // "grok-4.6 (< 200k prompt tokens)" and "grok-4.6 (>= 200k prompt tokens)". The comparator
    // is captured rather than assumed so a table that stops distinguishing the lanes, or renames
    // the unit, fails loudly instead of silently collapsing both lanes onto one rate.
    private static readonly Regex ModelCell = new(
        @"^(?<model>[A-Za-z0-9.\-]+)\s*\((?<comparator>[<≥>]=?)\s*(?<threshold>\d+)k\s+prompt\s+tokens\)$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1)
    );
    private static readonly Regex Money = new(
        @"^\$(?<amount>\d+(\.\d+)?)$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1)
    );
    private static readonly string[] PriceColumns =
    [
        "Model",
        "Context",
        "Input / 1M tokens",
        "Cached input / 1M tokens",
        "Output / 1M tokens",
    ];
    private readonly IClock _clock;
    private readonly FirstPartyDocumentFetcher _fetcher;
    private PricingSnapshotCandidate? _lastCandidate;

    public XaiPricingSource(IClock clock, IHttpClientFactory httpClientFactory)
    {
        _clock = clock;
        _fetcher = new FirstPartyDocumentFetcher(
            httpClientFactory.CreateClient(FirstPartyDocumentFetcher.HttpClientName),
            new Uri(PricingUrl),
            ["docs.x.ai"]
        );
    }

    internal XaiPricingSource(IClock clock, HttpMessageHandler? handler)
    {
        _clock = clock;
        _fetcher = new FirstPartyDocumentFetcher(new Uri(PricingUrl), ["docs.x.ai"], handler);
    }

    public string SourceId => PricingSourceIds.Xai;

    public void Dispose() => _fetcher.Dispose();

    public async Task<PricingSnapshotCandidate?> FetchAsync(CancellationToken cancellationToken)
    {
        var page = await _fetcher.FetchAsync(cancellationToken);
        var retrievedAt = _clock.GetCurrentInstant();
        var rawEvidence = $"{page.FinalUri.AbsoluteUri}\n{page.Content}";
        var candidate = PricingCandidate.Create(
            Provider.Xai,
            SourceId,
            retrievedAt,
            PricingUrl,
            rawEvidence,
            Parse(page.Content, retrievedAt)
        );
        if (_lastCandidate?.ContentHash == candidate.ContentHash)
        {
            return _lastCandidate;
        }

        return _lastCandidate = candidate;
    }

    public static XaiPriceCatalog Parse(string document, Instant retrievedAt)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.Contains(ThresholdFootnote, StringComparison.Ordinal))
        {
            throw new InvalidDataException("xAI long-context billing rule changed or is missing.");
        }

        var observedOn = retrievedAt.InUtc().Date;
        var (lanes, threshold) = ReadLanes(ReadTable(document));
        var entries = new List<XaiPriceEntry>();
        foreach (var (model, lane) in lanes)
        {
            if (lane.Standard is null || lane.Long is null)
            {
                throw new InvalidDataException("xAI pricing is missing a lane for a model.");
            }

            entries.Add(
                new XaiPriceEntry(
                    model,
                    [],
                    observedOn,
                    false,
                    lane.Standard.Input,
                    lane.Standard.CachedInput,
                    lane.Standard.Output,
                    lane.Long.Input,
                    lane.Long.CachedInput,
                    lane.Long.Output
                )
            );
        }

        var catalog = new XaiPriceCatalog(
            "USD",
            PricingUrl,
            retrievedAt,
            threshold,
            entries.OrderBy(entry => entry.Model, StringComparer.Ordinal).ToList()
        );
        catalog.Validate();
        return catalog;
    }

    private static (Dictionary<string, XaiLanes> Lanes, long Threshold) ReadLanes(IEnumerable<XaiRow> rows)
    {
        var lanes = new Dictionary<string, XaiLanes>(StringComparer.OrdinalIgnoreCase);
        long? threshold = null;
        foreach (var row in rows)
        {
            var (model, isLongContext, rowThreshold) = ReadModelCell(row.Model);
            if (threshold is not null && threshold != rowThreshold)
            {
                // The catalog stores one threshold for every model. Two different ones would make
                // the stored value wrong for at least one of them.
                throw new InvalidDataException("xAI pricing declares more than one long-context threshold.");
            }
            threshold = rowThreshold;
            var existing = lanes.GetValueOrDefault(model);
            if ((isLongContext ? existing.Long : existing.Standard) is not null)
            {
                throw new InvalidDataException("xAI pricing contains a duplicate model lane.");
            }
            var rates = new XaiRates(row.Input, row.CachedInput, row.Output);
            lanes[model] = isLongContext ? existing with { Long = rates } : existing with { Standard = rates };
        }
        if (lanes.Count == 0 || threshold is null)
        {
            throw new InvalidDataException("xAI pricing contains no models.");
        }
        return (lanes, threshold.Value);
    }

    private static (string Model, bool IsLongContext, long Threshold) ReadModelCell(string cell)
    {
        var match = ModelCell.Match(cell);
        if (!match.Success)
        {
            throw new InvalidDataException("xAI pricing contains an unrecognised model cell.");
        }
        return (
            match.Groups["model"].Value,
            match.Groups["comparator"].Value is "≥" or ">=",
            long.Parse(match.Groups["threshold"].Value, CultureInfo.InvariantCulture) * 1000
        );
    }

    private static IEnumerable<XaiRow> ReadTable(string document)
    {
        var lines = document.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var heading = Array.FindIndex(lines, line => line.Contains(TableHeading, StringComparison.Ordinal));
        if (heading < 0)
        {
            throw new InvalidDataException("xAI pricing is missing its text API table.");
        }

        var header = Array.FindIndex(lines, heading, line => line.TrimStart().StartsWith('|'));
        if (header < 0 || header + 1 >= lines.Length)
        {
            throw new InvalidDataException("xAI pricing is missing its text API table.");
        }

        var columns = Cells(lines[header]);
        if (!columns.SequenceEqual(PriceColumns, StringComparer.Ordinal))
        {
            // A reordered or renamed column would put the output rate in the input slot, which
            // reads as a valid catalog and misprices everything, so refuse the whole document.
            throw new InvalidDataException("xAI pricing columns changed.");
        }

        var rows = new List<XaiRow>();
        for (var index = header + 2; index < lines.Length; index++)
        {
            var line = lines[index].TrimStart();
            if (!line.StartsWith('|'))
            {
                break;
            }

            var cells = Cells(lines[index]);
            if (cells.Count != PriceColumns.Length)
            {
                throw new InvalidDataException("xAI pricing contains a malformed row.");
            }

            rows.Add(new XaiRow(cells[0], Amount(cells[2]), Amount(cells[3]), Amount(cells[4])));
        }

        return rows.Count == 0 ? throw new InvalidDataException("xAI pricing table is empty.") : rows;
    }

    private static List<string> Cells(string line)
    {
        var trimmed = line.Trim().Trim('|');
        return trimmed.Split('|').Select(cell => cell.Trim()).ToList();
    }

    private static decimal Amount(string cell)
    {
        var match = Money.Match(cell);
        if (!match.Success)
        {
            throw new InvalidDataException("xAI pricing contains an unparseable rate.");
        }

        return decimal.Parse(match.Groups["amount"].Value, CultureInfo.InvariantCulture);
    }

    private sealed record XaiRow(string Model, decimal Input, decimal CachedInput, decimal Output);

    private sealed record XaiRates(decimal Input, decimal CachedInput, decimal Output);

    private readonly record struct XaiLanes(XaiRates? Standard, XaiRates? Long);
}
