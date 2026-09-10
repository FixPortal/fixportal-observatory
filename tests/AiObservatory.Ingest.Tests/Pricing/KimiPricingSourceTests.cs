using System.Net;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing;
using AiObservatory.Data.Pricing.Catalogs;
using AiObservatory.Ingest.Pricing;
using AiObservatory.Ingest.Sources;
using AwesomeAssertions;
using NodaTime;
using NodaTime.Testing;

namespace AiObservatory.Ingest.Tests.Pricing;

public sealed class KimiPricingSourceTests
{
    private static readonly Instant RetrievedAt = Instant.FromUtc(2026, 8, 24, 12, 0);
    private static readonly LocalDate ObservedOn = new(2026, 8, 24);

    [Theory]
    [InlineData("kimi-k3", false, 0.30, 3.00, 15.00, null)]
    [InlineData("kimi-k2.7-code", false, 0.19, 0.95, 4.00, 0.60)]
    [InlineData("kimi-k2.7-code-highspeed", true, 0.38, 1.90, 8.00, null)]
    [InlineData("kimi-k2.6", false, 0.16, 0.95, 4.00, 0.60)]
    [InlineData("kimi-k2.5", false, 0.10, 0.60, 3.00, 0.60)]
    public void ParserKeepsExactlyTheFiveOfficialVariantsAndEligibleBatchMultiplier(
        string model,
        bool highSpeed,
        double cacheHit,
        double cacheMiss,
        double output,
        double? batchMultiplier
    )
    {
        var catalog = Parse(Fixtures());

        var entry = catalog.Resolve(model, highSpeed, ObservedOn);

        catalog.Entries.Should().HaveCount(5);
        entry.Should().NotBeNull();
        entry.CacheHit.Should().Be((decimal)cacheHit);
        entry.CacheMiss.Should().Be((decimal)cacheMiss);
        entry.Output.Should().Be((decimal)output);
        entry.BatchMultiplier.Should().Be(batchMultiplier is null ? null : (decimal)batchMultiplier.Value);
        entry.EffectiveDateIsProviderDeclared.Should().BeFalse();
    }

    [Theory]
    [InlineData("missing-heading")]
    [InlineData("duplicate-key")]
    [InlineData("overlap")]
    [InlineData("partial-batch")]
    [InlineData("non-usd")]
    [InlineData("zero-rate")]
    [InlineData("negative-rate")]
    [InlineData("unknown-column")]
    public void ParserRejectsMalformedOrAmbiguousCatalogs(string mutation)
    {
        var fixtures = Mutate(Fixtures(), mutation);

        var act = () => Parse(fixtures);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public async Task FetchValidatesTheOfficialIndexAndEveryRequiredPageBeforeReturningCandidate()
    {
        var fixtures = Fixtures();
        var pages = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["https://platform.kimi.ai/docs/llms.txt"] = Index(),
            ["https://platform.kimi.ai/docs/pricing/chat-k3.md"] = fixtures.K3,
            ["https://platform.kimi.ai/docs/pricing/chat-k27-code.md"] = fixtures.K27,
            ["https://platform.kimi.ai/docs/pricing/chat-k26.md"] = fixtures.K26,
            ["https://platform.kimi.ai/docs/pricing/chat-k25.md"] = fixtures.K25,
            ["https://platform.kimi.ai/docs/pricing/batch.md"] = fixtures.Batch,
        };
        var handler = new FirstPartyDocumentFetcherTests.RecordingHandler(
            (request, _) =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(pages[request.RequestUri!.AbsoluteUri]),
                }
        );
        var source = new KimiPricingSource(new FakeClock(RetrievedAt), handler);

        var candidate = await source.FetchAsync(TestContext.Current.CancellationToken);

        candidate!.Provider.Should().Be(Provider.Moonshot);
        candidate.SourceId.Should().Be(PricingSourceIds.Kimi);
        candidate.SourceUrl.Should().Be("https://platform.kimi.ai/docs/llms.txt");
        handler.Requests.Should().HaveCount(6);
        var catalog = PricingCatalogJson.Deserialize<KimiPriceCatalog>(candidate.NormalizedCatalog);
        ((Action)catalog.Validate).Should().NotThrow();
    }

    [Fact]
    public async Task FetchRejectsAnIndexThatNoLongerNamesEveryRequiredFirstPartyPage()
    {
        var handler = new FirstPartyDocumentFetcherTests.RecordingHandler(
            (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("# Kimi API Platform") }
        );
        var source = new KimiPricingSource(new FakeClock(RetrievedAt), handler);

        var act = () => source.FetchAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public void ParserToleratesAFinalRowWithoutTrailingComma()
    {
        // A page that renders its last row without the trailing comma the other rows carry
        // must not fail the whole source.
        const string k3Row =
            "[\"kimi-k3\", \"1M tokens\", <>{\"$\"}0.30</>, <>{\"$\"}3.00</>, <>{\"$\"}15.00</>, \"1,048,576 tokens\"],";
        var fixtures = Fixtures() with { K3 = Fixtures().K3.Replace(k3Row, k3Row[..^1], StringComparison.Ordinal) };

        var catalog = Parse(fixtures);

        catalog.Resolve("kimi-k3", false, ObservedOn).Should().NotBeNull();
    }

    [Fact]
    public void BundledCatalogContainsOnlyTheVerifiedFiveVariants()
    {
        var catalog = PricingCatalogJson.Deserialize<KimiPriceCatalog>(Bundle("kimi.json"));

        ((Action)catalog.Validate).Should().NotThrow();
        catalog.Should().BeEquivalentTo(Parse(Fixtures()), options => options.WithStrictOrdering());
        catalog.SourceUrl.Should().Be("https://platform.kimi.ai/docs/llms.txt");
        catalog.RetrievedAt.Should().Be(RetrievedAt);
        catalog.Entries.Should().HaveCount(5);
        catalog.Resolve("kimi-k2.6", false, ObservedOn)!.CacheHit.Should().Be(0.16m);
        catalog.Resolve("kimi-k2.5", false, ObservedOn)!.CacheHit.Should().Be(0.10m);
    }

    private static KimiPriceCatalog Parse(KimiFixtures fixtures) =>
        KimiPricingSource.Parse(fixtures.K3, fixtures.K27, fixtures.K26, fixtures.K25, fixtures.Batch, RetrievedAt);

    private static KimiFixtures Mutate(KimiFixtures fixtures, string mutation)
    {
        const string k3Row =
            "[\"kimi-k3\", \"1M tokens\", <>{\"$\"}0.30</>, <>{\"$\"}3.00</>, <>{\"$\"}15.00</>, \"1,048,576 tokens\"],";
        return mutation switch
        {
            "missing-heading" => fixtures with
            {
                K3 = fixtures.K3.Replace("## Product Pricing", "## Rates", StringComparison.Ordinal),
            },
            "duplicate-key" => fixtures with
            {
                K3 = fixtures.K3.Replace(k3Row, $"{k3Row}\n{k3Row}", StringComparison.Ordinal),
            },
            "overlap" => fixtures with
            {
                K27 = fixtures.K27.Replace(
                    "[\"kimi-k2.7-code\",",
                    $"{k3Row}\n[\"kimi-k2.7-code\",",
                    StringComparison.Ordinal
                ),
            },
            "partial-batch" => fixtures with
            {
                Batch = fixtures.Batch.Replace(
                    "[\"kimi-k2.6 (Batch)\", \"1M tokens\", \"$0.10\", \"$0.57\", \"$2.40\", \"262,144 tokens\"],",
                    "",
                    StringComparison.Ordinal
                ),
            },
            "non-usd" => fixtures with
            {
                K3 = fixtures.K3.Replace("<>{\"$\"}0.30</>", "<>{\"€\"}0.30</>", StringComparison.Ordinal),
            },
            "zero-rate" => fixtures with
            {
                K3 = fixtures.K3.Replace("<>{\"$\"}15.00</>", "<>{\"$\"}0.00</>", StringComparison.Ordinal),
            },
            "negative-rate" => fixtures with
            {
                K3 = fixtures.K3.Replace("<>{\"$\"}15.00</>", "<>{\"$\"}-15.00</>", StringComparison.Ordinal),
            },
            "unknown-column" => fixtures with
            {
                K3 = fixtures.K3.Replace(
                    "{ title: \"Unit\", width: \"12%\" },",
                    "{ title: \"Currency\", width: \"12%\" },\n{ title: \"Unit\", width: \"12%\" },",
                    StringComparison.Ordinal
                ),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
    }

    private static KimiFixtures Fixtures() =>
        new(
            Fixture("kimi-k3.md"),
            Fixture("kimi-k27-code.md"),
            Fixture("kimi-k26.md"),
            Fixture("kimi-k25.md"),
            Fixture("kimi-batch.md")
        );

    private static string Index() =>
        """
            # Kimi API Platform
            - [Flagship Model Kimi K3 Pricing](https://platform.kimi.ai/docs/pricing/chat-k3.md)
            - [Coding Model Kimi K2.7 Code Pricing](https://platform.kimi.ai/docs/pricing/chat-k27-code.md)
            - [Kimi K2.6 Model Pricing](https://platform.kimi.ai/docs/pricing/chat-k26.md)
            - [Multi-modal Model Kimi K2.5 Pricing](https://platform.kimi.ai/docs/pricing/chat-k25.md)
            - [BatchJob Pricing](https://platform.kimi.ai/docs/pricing/batch.md)
            """;

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Pricing", "Fixtures", name));

    private static string Bundle(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Pricing", "Bundled", name));

    private sealed record KimiFixtures(string K3, string K27, string K26, string K25, string Batch);
}
