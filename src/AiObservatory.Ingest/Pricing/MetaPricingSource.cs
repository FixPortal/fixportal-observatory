using System.Globalization;
using System.Text.Json;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing;
using AiObservatory.Data.Pricing.Catalogs;
using AiObservatory.Ingest.Sources;
using NodaTime;

namespace AiObservatory.Ingest.Pricing;

/// <summary>
/// Meta prices come from OpenRouter's public model catalogue rather than Meta's own docs.
/// OpenRouter is the route that bills this estate, its rate is therefore the one a Meta row
/// was actually charged, and it serves machine-readable JSON — where Meta's docs page is
/// rendered HTML whose rates would have to be scraped out of markup. The catalogue covers
/// every vendor OpenRouter carries; only the <c>meta/</c> ids are kept, and only those ids
/// form the stored evidence, so another vendor's repricing cannot churn Meta snapshots.
/// </summary>
public sealed class MetaPricingSource : IPricingSource, IDisposable
{
#pragma warning disable S1075 // This URL is the fixed trust boundary required by the pricing design.
    private const string CatalogUrl = "https://openrouter.ai/api/v1/models";
#pragma warning restore S1075
    private const string ModelPrefix = "meta/";
    private readonly IClock _clock;
    private readonly FirstPartyDocumentFetcher _fetcher;
    private PricingSnapshotCandidate? _lastCandidate;

    public MetaPricingSource(IClock clock, IHttpClientFactory httpClientFactory)
    {
        _clock = clock;
        _fetcher = new FirstPartyDocumentFetcher(
            httpClientFactory.CreateClient(FirstPartyDocumentFetcher.HttpClientName),
            new Uri(CatalogUrl),
            ["openrouter.ai"]
        );
    }

    internal MetaPricingSource(IClock clock, HttpMessageHandler? handler)
    {
        _clock = clock;
        _fetcher = new FirstPartyDocumentFetcher(new Uri(CatalogUrl), ["openrouter.ai"], handler);
    }

    public string SourceId => PricingSourceIds.MetaOpenRouter;

    public void Dispose() => _fetcher.Dispose();

    public async Task<PricingSnapshotCandidate?> FetchAsync(CancellationToken cancellationToken)
    {
        var page = await _fetcher.FetchAsync(cancellationToken);
        var retrievedAt = _clock.GetCurrentInstant();
        var (catalog, evidence) = Parse(page.Content, retrievedAt);
        var candidate = PricingCandidate.Create(
            Provider.Meta,
            SourceId,
            retrievedAt,
            CatalogUrl,
            $"{page.FinalUri.AbsoluteUri}\n{evidence}",
            catalog
        );
        if (_lastCandidate?.ContentHash == candidate.ContentHash)
        {
            return _lastCandidate;
        }

        return _lastCandidate = candidate;
    }

    public static (MetaPriceCatalog Catalog, string Evidence) Parse(string document, Instant retrievedAt)
    {
        ArgumentNullException.ThrowIfNull(document);
        using var parsed = JsonDocument.Parse(document);
        if (
            parsed.RootElement.ValueKind != JsonValueKind.Object
            || !parsed.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array
        )
        {
            throw new InvalidDataException("The OpenRouter catalogue is not in its documented shape.");
        }

        var observedOn = retrievedAt.InUtc().Date;
        var entries = new List<MetaPriceEntry>();
        var evidence = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in data.EnumerateArray())
        {
            if (
                model.ValueKind != JsonValueKind.Object
                || !model.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.String
            )
            {
                throw new InvalidDataException("The OpenRouter catalogue contains a model without an id.");
            }

            var modelId = id.GetString()!;
            if (!modelId.StartsWith(ModelPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!seen.Add(modelId))
            {
                throw new InvalidDataException("The OpenRouter catalogue lists a Meta model twice.");
            }

            if (!model.TryGetProperty("pricing", out var pricing) || pricing.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("The OpenRouter catalogue lists a Meta model without pricing.");
            }

            entries.Add(
                new MetaPriceEntry(
                    modelId,
                    observedOn,
                    false,
                    PerMillion(pricing, "prompt", required: true)!.Value,
                    PerMillion(pricing, "completion", required: true)!.Value,
                    PerMillion(pricing, "input_cache_read", required: false)
                )
            );
            evidence.Add(model.GetRawText());
        }

        if (entries.Count == 0)
        {
            // Meta disappearing from OpenRouter is a real possibility, but it is indistinguishable
            // here from a response shape change, and an empty catalog would retire every Meta
            // price. Fail the refresh and leave the last good snapshot active.
            throw new InvalidDataException("The OpenRouter catalogue lists no Meta models.");
        }

        var catalog = new MetaPriceCatalog(
            "USD",
            CatalogUrl,
            retrievedAt,
            entries.OrderBy(entry => entry.Model, StringComparer.Ordinal).ToList()
        );
        catalog.Validate();
        return (catalog, string.Join("\n", evidence.Order(StringComparer.Ordinal)));
    }

    /// <summary>
    /// OpenRouter quotes USD per token as a decimal string. Rates are small enough that binary
    /// floating point would not round-trip them, so the string is parsed straight to decimal.
    /// </summary>
    private static decimal? PerMillion(JsonElement pricing, string name, bool required)
    {
        if (!pricing.TryGetProperty(name, out var rate) || rate.ValueKind == JsonValueKind.Null)
        {
            return required ? throw new InvalidDataException($"The OpenRouter rate '{name}' is missing.") : null;
        }

        var text = rate.ValueKind switch
        {
            JsonValueKind.String => rate.GetString(),
            JsonValueKind.Number => rate.GetRawText(),
            _ => throw new InvalidDataException($"The OpenRouter rate '{name}' is not a number."),
        };

        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var perToken))
        {
            throw new InvalidDataException($"The OpenRouter rate '{name}' is not a number.");
        }

        return perToken * 1_000_000m;
    }
}
