using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing;
using AiObservatory.Data.Pricing.Catalogs;
using AiObservatory.Ingest.Sources;

namespace AiObservatory.Ingest.Pricing;

public sealed class BundledPricingCatalogLoader
{
    private readonly PricingSnapshotStore _store;
    private readonly PricingRepricingService _repricing;
    private readonly ILogger<BundledPricingCatalogLoader> _logger;
    private readonly string _baseDirectory;

    public BundledPricingCatalogLoader(
        PricingSnapshotStore store,
        PricingRepricingService repricing,
        ILogger<BundledPricingCatalogLoader> logger
    )
        : this(store, repricing, logger, AppContext.BaseDirectory) { }

    internal BundledPricingCatalogLoader(
        PricingSnapshotStore store,
        PricingRepricingService repricing,
        ILogger<BundledPricingCatalogLoader> logger,
        string baseDirectory
    )
    {
        _store = store;
        _repricing = repricing;
        _logger = logger;
        _baseDirectory = baseDirectory;
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        await LoadAsync<OpenAiPriceCatalog>(
            Provider.OpenAI,
            PricingSourceIds.OpenAi,
            "openai.json",
            static catalog => catalog.SourceUrl,
            static catalog => catalog.RetrievedAt,
            cancellationToken
        );
        await LoadAsync<AnthropicPriceCatalog>(
            Provider.Anthropic,
            PricingSourceIds.Claude,
            "claude.json",
            static catalog => catalog.SourceUrl,
            static catalog => catalog.RetrievedAt,
            cancellationToken
        );
        await LoadAsync<KimiPriceCatalog>(
            Provider.Moonshot,
            PricingSourceIds.Kimi,
            "kimi.json",
            static catalog => catalog.SourceUrl,
            static catalog => catalog.RetrievedAt,
            cancellationToken
        );
        await LoadAsync<GooglePriceCatalog>(
            Provider.Google,
            PricingSourceIds.GoogleCloudCatalog,
            "google.json",
            static catalog => catalog.SourceUrl,
            static catalog => catalog.RetrievedAt,
            cancellationToken
        );
        await LoadAsync<GeminiDeveloperPriceCatalog>(
            Provider.Google,
            PricingSourceIds.GeminiDeveloperApi,
            "gemini-developer-api.json",
            static catalog => catalog.SourceUrl,
            static catalog => catalog.RetrievedAt,
            cancellationToken,
            replaceActive: true
        );
    }

    private async Task LoadAsync<T>(
        Provider provider,
        string sourceId,
        string fileName,
        Func<T, string> getSourceUrl,
        Func<T, NodaTime.Instant> getRetrievedAt,
        CancellationToken cancellationToken,
        bool replaceActive = false
    )
    {
        try
        {
            var raw = await File.ReadAllTextAsync(
                Path.Combine(_baseDirectory, "Pricing", "Bundled", fileName),
                cancellationToken
            );
            var catalog = PricingCatalogJson.Deserialize<T>(raw);
            var sourceUrl = getSourceUrl(catalog);
            var retrievedAt = getRetrievedAt(catalog);
            var candidate = PricingCandidate.Create(provider, sourceId, retrievedAt, sourceUrl, raw, catalog);

            Task Reprice(PricingSnapshot _, CancellationToken callbackCt) =>
                _repricing.RepriceProviderAsync(provider, callbackCt);
            var result = replaceActive
                ? await _store.ActivateAsync(candidate, cancellationToken, Reprice)
                : await _store.ActivateIfMissingAsync(candidate, cancellationToken, Reprice);
            // ponytail: Observatory volume is modest; add a calculator-version checkpoint if daily scans become material.
            if (result == PricingActivationResult.Unchanged)
            {
                await _repricing.RepriceProviderAsync(provider, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogLoadFailure(sourceId, exception);
        }
    }

    private void LogLoadFailure(string sourceId, Exception exception)
    {
        var error = exception.Message.Replace(_baseDirectory, "<bundle-directory>", StringComparison.OrdinalIgnoreCase);
        _logger.LogError(
            "{SourceId} bundled pricing load failed: {Error}",
            sourceId,
            ProviderPollingWorkerService.SanitizeError(error)
        );
    }
}
