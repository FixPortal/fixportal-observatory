using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiObservatory.Data.Pricing;
using AiObservatory.Data.Pricing.Catalogs;
using AwesomeAssertions;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;

namespace AiObservatory.Data.Tests.Pricing;

public sealed class PricingSnapshotCandidateTests
{
    private static readonly Instant RetrievedAt = Instant.FromUtc(2026, 8, 24, 20, 0);
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(
        JsonSerializerDefaults.Web
    ).ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);

    [Fact]
    public void ContentHashIsStableAcrossARetrievalThatOnlyRestampsFetchDerivedDates()
    {
        // Live sources stamp retrievedAt and every assumed (non-provider-declared) effectiveFrom
        // with the fetch date, so both change on every refresh of unchanged evidence. Hashing
        // them would make each daily refresh look like new content.
        const string evidence = "unchanged provider page";
        var fetched = Catalog(new LocalDate(2026, 8, 24), 1m, declared: false, RetrievedAt);
        var refetched = Catalog(
            new LocalDate(2026, 9, 24),
            1m,
            declared: false,
            RetrievedAt.Plus(Duration.FromDays(31))
        );

        PricingSnapshotCandidate
            .ComputeContentHash(evidence, Json(refetched))
            .Should()
            .Be(PricingSnapshotCandidate.ComputeContentHash(evidence, Json(fetched)));
    }

    [Fact]
    public void ContentHashChangesWhenTheEvidenceOrTheRatesChange()
    {
        const string evidence = "provider page";
        var original = Json(Catalog(new LocalDate(2026, 8, 24), 1m, declared: false, RetrievedAt));
        var repriced = Json(Catalog(new LocalDate(2026, 8, 24), 2m, declared: false, RetrievedAt));

        var hash = PricingSnapshotCandidate.ComputeContentHash(evidence, original);
        PricingSnapshotCandidate.ComputeContentHash(evidence, repriced).Should().NotBe(hash);
        PricingSnapshotCandidate.ComputeContentHash("changed provider page", original).Should().NotBe(hash);
    }

    [Fact]
    public void ContentHashTreatsAProviderDeclaredEffectiveDateAsContent()
    {
        // A provider-announced date is real content (it gates when a price applies), unlike an
        // assumed date re-stamped by every fetch.
        const string evidence = "provider page";
        var announced = Catalog(new LocalDate(2026, 9, 1), 1m, declared: true, RetrievedAt);
        var postponed = Catalog(new LocalDate(2026, 10, 1), 1m, declared: true, RetrievedAt);

        PricingSnapshotCandidate
            .ComputeContentHash(evidence, Json(announced))
            .Should()
            .NotBe(PricingSnapshotCandidate.ComputeContentHash(evidence, Json(postponed)));
    }

    [Fact]
    public void ContentHashFallsBackToTheUnmodifiedCatalogWhenItIsNotJson()
    {
        // Validation owns the malformed-catalog error; identity still computes from the raw
        // string so that error path stays intact.
        var hash = PricingSnapshotCandidate.ComputeContentHash("evidence", "not json {");

        hash.Should().NotBe(PricingSnapshotCandidate.ComputeContentHash("evidence", "other {"));
    }

    [Theory]
    [InlineData("[1,2,3]")] // array root
    [InlineData("\"just a string\"")] // scalar root
    [InlineData("42")] // scalar root
    [InlineData("null")] // null node: JsonNode.Parse returns null
    public void ContentHashFallsBackToTheUnmodifiedCatalogForANonObjectJsonRoot(string catalog)
    {
        // AsObject() throws InvalidOperationException for these roots — not JsonException — so
        // it escaped the malformed-JSON catch and crashed hash computation before validation
        // could report. The fallback hashes the unmodified string instead.
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("evidence" + '\n' + catalog)));

        PricingSnapshotCandidate.ComputeContentHash("evidence", catalog).Should().Be(expected);
    }

    private static OpenAiPriceCatalog Catalog(
        LocalDate effectiveFrom,
        decimal input,
        bool declared,
        Instant retrievedAt
    ) =>
        new(
            "USD",
            "https://developers.openai.com/api/docs/pricing.md",
            retrievedAt,
            [
                new OpenAiPriceEntry(
                    "gpt-5.4",
                    ["gpt-5.4"],
                    effectiveFrom,
                    declared,
                    "standard",
                    "short",
                    "global",
                    input,
                    0.25m,
                    10m,
                    null
                ),
            ]
        );

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
}
