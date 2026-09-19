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

    // Rates as published on 2026-09-19. kimi-k2.5 is deliberately absent: Moonshot retired it
    // when it consolidated four per-model pages into chat.md, and this source no longer pins
    // an exact roster. No kimi-k2.5 usage was ever recorded in this Observatory.
    [Theory]
    [InlineData("kimi-k3", false, 0.30, 3.00, 15.00, null)]
    [InlineData("kimi-k2.7-code", false, 0.19, 0.95, 4.00, 0.60)]
    [InlineData("kimi-k2.7-code-highspeed", true, 0.38, 1.90, 8.00, null)]
    [InlineData("kimi-k2.6", false, 0.16, 0.95, 4.00, 0.60)]
    public void ParserReadsEveryPublishedVariantAndTheEligibleBatchMultiplier(
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

        catalog.Entries.Should().HaveCount(4);
        entry.Should().NotBeNull();
        entry.CacheHit.Should().Be((decimal)cacheHit);
        entry.CacheMiss.Should().Be((decimal)cacheMiss);
        entry.Output.Should().Be((decimal)output);
        entry.BatchMultiplier.Should().Be(batchMultiplier is null ? null : (decimal)batchMultiplier.Value);
        entry.EffectiveDateIsProviderDeclared.Should().BeFalse();
    }

    // The consolidation is the regression this source failed on for 61 consecutive days: the
    // index stopped naming chat-k3.md, chat-k27-code.md, chat-k26.md and chat-k25.md, and
    // ValidateIndex required each of them exactly once. The fixture here is the real
    // llms.txt, so the test fails again if the parser is ever re-pinned to pages that the
    // published index does not list.
    [Fact]
    public void IndexFixtureNamesOnlyTheConsolidatedPagesTheParserRequires()
    {
        var index = Fixture("kimi-llms.txt");

        index.Should().Contain("https://platform.kimi.ai/docs/pricing/chat.md");
        index.Should().Contain("https://platform.kimi.ai/docs/pricing/batch.md");
        index.Should().NotContain("https://platform.kimi.ai/docs/pricing/chat-k3.md");
        index.Should().NotContain("https://platform.kimi.ai/docs/pricing/chat-k25.md");
    }

    [Theory]
    [InlineData("missing-heading")]
    [InlineData("duplicate-key")]
    [InlineData("batch-unknown-model")]
    [InlineData("non-usd")]
    [InlineData("zero-rate")]
    [InlineData("negative-rate")]
    [InlineData("unknown-column")]
    [InlineData("batch-multiplier-broken")]
    [InlineData("required-model-missing")]
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
            ["https://platform.kimi.ai/docs/llms.txt"] = Fixture("kimi-llms.txt"),
            ["https://platform.kimi.ai/docs/pricing/chat.md"] = fixtures.Chat,
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
        handler.Requests.Should().HaveCount(3);
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
        var fixtures = Fixtures() with
        {
            Chat = Fixtures().Chat.Replace(K3Row, K3Row[..^1], StringComparison.Ordinal),
        };

        var catalog = Parse(fixtures);

        catalog.Resolve("kimi-k3", false, ObservedOn).Should().NotBeNull();
    }

    [Fact]
    public void BundledCatalogMatchesThePublishedVariants()
    {
        var catalog = PricingCatalogJson.Deserialize<KimiPriceCatalog>(Bundle("kimi.json"));

        ((Action)catalog.Validate).Should().NotThrow();
        catalog.Should().BeEquivalentTo(Parse(Fixtures()), options => options.WithStrictOrdering());
        catalog.SourceUrl.Should().Be("https://platform.kimi.ai/docs/llms.txt");
        catalog.RetrievedAt.Should().Be(RetrievedAt);
        catalog.Entries.Should().HaveCount(4);
        catalog.Resolve("kimi-k2.6", false, ObservedOn)!.CacheHit.Should().Be(0.16m);
        catalog.Resolve("kimi-k3", false, ObservedOn)!.CacheHit.Should().Be(0.30m);
    }

    private const string K3Row =
        "[\"kimi-k3\", \"1M tokens\", <>{\"$\"}0.30</>, <>{\"$\"}3.00</>, <>{\"$\"}15.00</>, \"1,048,576 tokens\"],";

    private static KimiPriceCatalog Parse(KimiFixtures fixtures) =>
        KimiPricingSource.Parse(fixtures.Chat, fixtures.Batch, RetrievedAt);

    private static KimiFixtures Mutate(KimiFixtures fixtures, string mutation) =>
        mutation switch
        {
            "missing-heading" => fixtures with
            {
                Chat = fixtures.Chat.Replace("## Model Pricing", "## Rates", StringComparison.Ordinal),
            },
            "duplicate-key" => fixtures with
            {
                Chat = fixtures.Chat.Replace(K3Row, $"{K3Row}\n{K3Row}", StringComparison.Ordinal),
            },
            // Moonshot withdrawing Batch for a model is a product decision and no longer an
            // error, but a Batch row naming a model the chat table never priced means the two
            // pages disagree, and that must still fail.
            "batch-unknown-model" => fixtures with
            {
                Batch = fixtures.Batch.Replace(
                    "\"kimi-k2.6 (Batch)\"",
                    "\"kimi-k9.9 (Batch)\"",
                    StringComparison.Ordinal
                ),
            },
            "non-usd" => fixtures with
            {
                Chat = fixtures.Chat.Replace("<>{\"$\"}0.30</>", "<>{\"€\"}0.30</>", StringComparison.Ordinal),
            },
            "zero-rate" => fixtures with
            {
                Chat = fixtures.Chat.Replace("<>{\"$\"}15.00</>", "<>{\"$\"}0.00</>", StringComparison.Ordinal),
            },
            "negative-rate" => fixtures with
            {
                Chat = fixtures.Chat.Replace("<>{\"$\"}15.00</>", "<>{\"$\"}-15.00</>", StringComparison.Ordinal),
            },
            "unknown-column" => fixtures with
            {
                Chat = fixtures.Chat.Replace(
                    "{ title: \"Unit\", width: \"12%\" },",
                    "{ title: \"Currency\", width: \"12%\" },\n{ title: \"Unit\", width: \"12%\" },",
                    StringComparison.Ordinal
                ),
            },
            // Deriving the batch roster from the page must not weaken the 60% cross-check.
            "batch-multiplier-broken" => fixtures with
            {
                Batch = fixtures.Batch.Replace("\"$0.114\"", "\"$0.99\"", StringComparison.Ordinal),
            },
            // Dropping a model this estate actually routes work to is still an outage, even
            // though an unfamiliar new model is not.
            "required-model-missing" => fixtures with
            {
                Chat = fixtures.Chat.Replace(K3Row, "", StringComparison.Ordinal),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };

    private static KimiFixtures Fixtures() => new(Fixture("kimi-chat.md"), Fixture("kimi-batch.md"));

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Pricing", "Fixtures", name));

    private static string Bundle(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Pricing", "Bundled", name));

    private sealed record KimiFixtures(string Chat, string Batch);
}
