using System.Globalization;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing;
using AiObservatory.Data.Pricing.Catalogs;
using AiObservatory.Ingest.Sources;
using NodaTime;

namespace AiObservatory.Ingest.Pricing;

public sealed class OpenAiPricingSource : IPricingSource, IDisposable
{
#pragma warning disable S1075 // The source URL is the fixed trust boundary required by the pricing design.
    private const string SourceUrl = "https://developers.openai.com/api/docs/pricing.md";
#pragma warning restore S1075
    private static readonly string[] PriceColumns =
    [
        "Model",
        "Short context input",
        "Short context cached input",
        "Short context cache writes",
        "Short context output",
        "Long context input",
        "Long context cached input",
        "Long context cache writes",
        "Long context output",
    ];
    private static readonly (string Model, string Processing, string Context)[] RequiredLanes =
    [
        ("gpt-5.6-sol", "standard", "short"),
        ("gpt-5.6-sol", "standard", "long"),
        ("gpt-5.4", "standard", "short"),
        ("gpt-5.4", "standard", "long"),
        ("gpt-5.6-sol", "batch", "short"),
        ("gpt-5.6-sol", "batch", "long"),
        ("gpt-5.4", "batch", "short"),
        ("gpt-5.4", "batch", "long"),
        ("gpt-5.6-sol", "flex", "short"),
        ("gpt-5.6-sol", "flex", "long"),
        ("gpt-5.4", "flex", "short"),
        ("gpt-5.4", "flex", "long"),
        ("gpt-5.6-sol", "fast", "short"),
        ("gpt-5.6-sol", "fast", "long"),
        ("gpt-5.4", "fast", "short"),
    ];
    private readonly IClock _clock;
    private readonly FirstPartyDocumentFetcher _fetcher;
    private PricingSnapshotCandidate? _lastCandidate;

    public OpenAiPricingSource(IClock clock, IHttpClientFactory httpClientFactory)
    {
        _clock = clock;
        _fetcher = new FirstPartyDocumentFetcher(
            httpClientFactory.CreateClient(FirstPartyDocumentFetcher.HttpClientName),
            new Uri(SourceUrl),
            ["developers.openai.com"]
        );
    }

    internal OpenAiPricingSource(IClock clock, HttpMessageHandler? handler)
    {
        _clock = clock;
        _fetcher = new FirstPartyDocumentFetcher(new Uri(SourceUrl), ["developers.openai.com"], handler);
    }

    public string SourceId => PricingSourceIds.OpenAi;

    public void Dispose() => _fetcher.Dispose();

    public async Task<PricingSnapshotCandidate?> FetchAsync(CancellationToken cancellationToken)
    {
        var document = await _fetcher.FetchAsync(cancellationToken);

        var retrievedAt = _clock.GetCurrentInstant();
        var raw = document.Content;
        var candidate = PricingCandidate.Create(
            Provider.OpenAI,
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

    public static OpenAiPriceCatalog Parse(string document, Instant retrievedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(document);
        var lines = Lines(document);
        ValidateUnitDeclaration(lines);
        var observedOn = retrievedAt.InUtc().Date;
        var entries = new List<OpenAiPriceEntry>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ParseTable(lines, "### Standard pricing data", "standard", observedOn, entries, keys);
        ParseTable(lines, "### Batch pricing data", "batch", observedOn, entries, keys);
        ParseTable(lines, "### Flex pricing data", "flex", observedOn, entries, keys);
        ParseTable(lines, "### Fast pricing data", "fast", observedOn, entries, keys);
        if (
            RequiredLanes.Any(required =>
                !keys.Contains(string.Join('\u001f', required.Model, required.Processing, required.Context, "global"))
            )
        )
        {
            throw new InvalidDataException("OpenAI pricing is missing a required current model lane.");
        }

        var catalog = new OpenAiPriceCatalog("USD", SourceUrl, retrievedAt, entries);
        catalog.Validate();
        return catalog;
    }

    private static void ParseTable(
        IReadOnlyList<string> lines,
        string heading,
        string processing,
        LocalDate observedOn,
        List<OpenAiPriceEntry> entries,
        HashSet<string> keys
    )
    {
        var headingIndex = SingleLine(lines, heading);
        var headerIndex = NextContentLine(lines, headingIndex + 1);
        if (!Cells(lines[headerIndex]).SequenceEqual(PriceColumns, StringComparer.Ordinal))
        {
            throw new InvalidDataException($"OpenAI {processing} pricing columns changed.");
        }

        var separatorIndex = NextContentLine(lines, headerIndex + 1);
        ValidateSeparator(lines[separatorIndex], PriceColumns.Length);
        var rowIndex = separatorIndex + 1;
        var parsedRows = 0;
        while (rowIndex < lines.Count && lines[rowIndex].TrimStart().StartsWith('|'))
        {
            var cells = Cells(lines[rowIndex]);
            if (cells.Length != PriceColumns.Length)
            {
                throw new InvalidDataException($"OpenAI {processing} pricing row is partial.");
            }

            var model = NormalizeModel(cells[0]);
            AddLane(cells, model, processing, "short", 1, observedOn, entries, keys);
            AddLane(cells, model, processing, "long", 5, observedOn, entries, keys);
            parsedRows++;
            rowIndex++;
        }

        if (parsedRows == 0 || !entries.Any(entry => entry.Processing == processing))
        {
            throw new InvalidDataException($"OpenAI {processing} pricing table is empty.");
        }
    }

    private static void AddLane(
        IReadOnlyList<string> cells,
        string model,
        string processing,
        string context,
        int offset,
        LocalDate observedOn,
        List<OpenAiPriceEntry> entries,
        HashSet<string> keys
    )
    {
        var input = ParseRate(cells[offset]);
        var cachedInput = ParseRate(cells[offset + 1]);
        var cacheWrite = ParseRate(cells[offset + 2]);
        var output = ParseRate(cells[offset + 3]);
        if (input is null && cachedInput is null && output is null && cells[offset + 2] == "-")
        {
            return;
        }

        if (input is null || output is null)
        {
            throw new InvalidDataException("OpenAI pricing contains a partial context lane.");
        }

        var key = string.Join('\u001f', model, processing, context, "global");
        if (!keys.Add(key))
        {
            throw new InvalidDataException("OpenAI pricing contains a duplicate or overlapping key.");
        }

        entries.Add(
            new OpenAiPriceEntry(
                model,
                [model],
                observedOn,
                false,
                processing,
                context,
                "global",
                input.Value,
                cachedInput,
                output.Value,
                cacheWrite
            )
        );
    }

    private static decimal? ParseRate(string value)
    {
        if (value == "-")
        {
            return null;
        }

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
            throw new InvalidDataException("OpenAI pricing contains a non-USD or non-positive rate.");
        }

        return rate;
    }

    private static string NormalizeModel(string value)
    {
        var suffix = value.IndexOf(" (", StringComparison.Ordinal);
        var model = (suffix < 0 ? value : value[..suffix]).Trim();
        if (model.Length == 0)
        {
            throw new InvalidDataException("OpenAI pricing contains an empty model.");
        }

        return model;
    }

    private static int SingleLine(IReadOnlyList<string> lines, string expected)
    {
        var matches = Enumerable.Range(0, lines.Count).Where(index => lines[index].Trim() == expected).ToList();
        return matches.Count == 1
            ? matches[0]
            : throw new InvalidDataException($"OpenAI pricing requires exactly one '{expected}' heading.");
    }

    private static void ValidateUnitDeclaration(IReadOnlyList<string> lines)
    {
        var standardHeading = SingleLine(lines, "### Standard pricing data");
        var declarations = Enumerable
            .Range(0, standardHeading)
            .Select(index => lines[index].Trim())
            .Where(line => line.StartsWith("Prices per ", StringComparison.Ordinal))
            .ToList();
        if (declarations.Count != 1 || declarations[0] != "Prices per 1M tokens.")
        {
            throw new InvalidDataException(
                "OpenAI pricing requires exactly one 'Prices per 1M tokens.' declaration before its tables."
            );
        }
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

        throw new InvalidDataException("OpenAI pricing ended before its required table.");
    }

    private static string[] Cells(string line)
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith('|') || !trimmed.EndsWith('|'))
        {
            throw new InvalidDataException("OpenAI pricing table shape changed.");
        }

        return trimmed.Split('|')[1..^1].Select(cell => cell.Trim()).ToArray();
    }

    private static void ValidateSeparator(string line, int columns)
    {
        var cells = Cells(line);
        if (cells.Length != columns || cells.Any(cell => cell.Length < 3 || cell.Any(character => character != '-')))
        {
            throw new InvalidDataException("OpenAI pricing table separator changed.");
        }
    }

    private static string[] Lines(string document) =>
        document.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
}
