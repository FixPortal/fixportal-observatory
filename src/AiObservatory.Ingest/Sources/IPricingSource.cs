using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing;
using AiObservatory.Data.Pricing.Catalogs;
using NodaTime;

namespace AiObservatory.Ingest.Sources;

public interface IPricingSource
{
    string SourceId { get; }
    Task<PricingSnapshotCandidate?> FetchAsync(CancellationToken cancellationToken);
}

public sealed record PricingSourceDefinition(string SourceId, bool IsConfigured, Duration ExpectedRefreshInterval);

internal static class PricingCandidate
{
    public static PricingSnapshotCandidate Create<T>(
        Provider provider,
        string sourceId,
        Instant retrievedAt,
        string sourceUrl,
        string rawEvidence,
        T catalog
    )
    {
        Validate(catalog);
        var normalized = PricingCatalogJson.Serialize(catalog);
        return new PricingSnapshotCandidate(
            provider,
            sourceId,
            retrievedAt,
            sourceUrl,
            PricingSnapshotCandidate.ComputeContentHash(rawEvidence, normalized),
            rawEvidence,
            normalized
        );
    }

    private static void Validate<T>(T catalog)
    {
        switch (catalog)
        {
            case OpenAiPriceCatalog openAi:
                openAi.Validate();
                break;
            case AnthropicPriceCatalog anthropic:
                anthropic.Validate();
                break;
            case KimiPriceCatalog kimi:
                kimi.Validate();
                break;
            case GooglePriceCatalog google:
                google.Validate();
                break;
            case GeminiDeveloperPriceCatalog gemini:
                gemini.Validate();
                break;
            default:
                throw new ArgumentException("Unknown pricing catalog type.", nameof(catalog));
        }
    }
}
