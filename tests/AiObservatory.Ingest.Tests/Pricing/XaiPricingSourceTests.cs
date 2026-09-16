using System.Net;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing;
using AiObservatory.Data.Pricing.Catalogs;
using AiObservatory.Ingest.Pricing;
using AwesomeAssertions;
using NodaTime;
using NodaTime.Testing;

namespace AiObservatory.Ingest.Tests.Pricing;

public sealed class XaiPricingSourceTests
{
    private const string PricingUrl = "https://docs.x.ai/developers/pricing.md";
    private static readonly Instant RetrievedAt = Instant.FromUtc(2026, 9, 16, 12, 0);
    private static readonly LocalDate ObservedOn = new(2026, 9, 16);

    [Theory]
    [InlineData("grok-4.6", 2.00, 0.50, 6.00, 4.00, 1.00, 12.00)]
    [InlineData("grok-4.5", 2.00, 0.30, 6.00, 4.00, 0.60, 12.00)]
    [InlineData("grok-4.3", 1.25, 0.20, 2.50, 2.50, 0.40, 5.00)]
    [InlineData("grok-build-0.1", 1.00, 0.20, 2.00, 2.00, 0.40, 4.00)]
    [InlineData("grok-4.20-0309-reasoning", 1.25, 0.20, 2.50, 2.50, 0.40, 5.00)]
    public void ParserFoldsTheTwoPublishedRowsOfAModelIntoOneEntryWithBothLanes(
        string model,
        double input,
        double cachedInput,
        double output,
        double longInput,
        double longCachedInput,
        double longOutput
    )
    {
        var catalog = XaiPricingSource.Parse(Fixture(), RetrievedAt);

        var entry = catalog.Resolve(model, ObservedOn);

        entry.Should().NotBeNull();
        entry.Input.Should().Be((decimal)input);
        entry.CachedInput.Should().Be((decimal)cachedInput);
        entry.Output.Should().Be((decimal)output);
        entry.LongContextInput.Should().Be((decimal)longInput);
        entry.LongContextCachedInput.Should().Be((decimal)longCachedInput);
        entry.LongContextOutput.Should().Be((decimal)longOutput);
        entry.EffectiveDateIsProviderDeclared.Should().BeFalse();
    }

    [Fact]
    public void ParserKeepsEveryPublishedTextModelAndTheDeclaredThreshold()
    {
        var catalog = XaiPricingSource.Parse(Fixture(), RetrievedAt);

        catalog.Entries.Should().HaveCount(7);
        catalog.LongContextThresholdTokens.Should().Be(200_000);
        catalog.Currency.Should().Be("USD");
        catalog.SourceUrl.Should().Be(PricingUrl);
    }

    [Fact]
    public void ParserIgnoresTheImageAndVoiceTablesThatFollowTheTextTable()
    {
        var catalog = XaiPricingSource.Parse(Fixture(), RetrievedAt);

        catalog.Entries.Should().AllSatisfy(entry => entry.Model.Should().NotContain("imagine"));
        catalog.Resolve("grok-imagine-image-2.0", ObservedOn).Should().BeNull();
    }

    /// <summary>
    /// `grok-4.6` is a prefix of `grok-4.6-build`, the model the Grok CLI actually reports for
    /// most of its work and which the published table does not price. Prefix matching would
    /// price it at 4.6's rates; the contract is that it stays unpriced and visible instead.
    /// </summary>
    [Theory]
    [InlineData("grok-4.6-build")]
    [InlineData("grok-4.6-mini")]
    [InlineData("grok")]
    public void ResolveRefusesAModelThatOnlySharesAPrefixWithAPublishedOne(string model)
    {
        var catalog = XaiPricingSource.Parse(Fixture(), RetrievedAt);

        catalog.Resolve(model, ObservedOn).Should().BeNull();
    }

    [Theory]
    [InlineData("missing-footnote")]
    [InlineData("missing-heading")]
    [InlineData("renamed-column")]
    [InlineData("reordered-columns")]
    [InlineData("single-lane")]
    [InlineData("duplicate-lane")]
    [InlineData("two-thresholds")]
    [InlineData("unparseable-rate")]
    [InlineData("inverted-lanes")]
    [InlineData("short-row")]
    public void ParserRejectsMalformedOrAmbiguousCatalogs(string mutation)
    {
        var document = Mutate(Fixture(), mutation);

        var act = () => XaiPricingSource.Parse(document, RetrievedAt);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public async Task FetchReturnsACandidateWhoseCatalogMatchesTheParsedDocument()
    {
        var document = Fixture();
        using var source = new XaiPricingSource(new FakeClock(RetrievedAt), new StubHandler(document));

        var candidate = await source.FetchAsync(TestContext.Current.CancellationToken);

        candidate.Should().NotBeNull();
        candidate.Provider.Should().Be(Provider.Xai);
        candidate.SourceId.Should().Be(PricingSourceIds.Xai);
        candidate.SourceUrl.Should().Be(PricingUrl);
        candidate.RetrievedAt.Should().Be(RetrievedAt);
        candidate.RawEvidence.Should().Contain(document);
        PricingCatalogJson
            .Deserialize<XaiPriceCatalog>(candidate.NormalizedCatalog)
            .Should()
            .BeEquivalentTo(XaiPricingSource.Parse(document, RetrievedAt), options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task FetchReturnsTheSameCandidateInstanceWhenTheDocumentHasNotChanged()
    {
        using var source = new XaiPricingSource(new FakeClock(RetrievedAt), new StubHandler(Fixture()));

        var first = await source.FetchAsync(TestContext.Current.CancellationToken);
        var second = await source.FetchAsync(TestContext.Current.CancellationToken);

        second.Should().BeSameAs(first);
    }

    [Fact]
    public void BundledCatalogMatchesTheRatesThePublishedTableCarries()
    {
        var parsed = XaiPricingSource.Parse(Fixture(), RetrievedAt);
        var bundled = PricingCatalogJson.Deserialize<XaiPriceCatalog>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Pricing", "Bundled", "xai.json"))
        );

        bundled.LongContextThresholdTokens.Should().Be(parsed.LongContextThresholdTokens);
        bundled
            .Entries.Select(entry => (entry.Model, entry.Input, entry.CachedInput, entry.Output))
            .Should()
            .BeEquivalentTo(
                parsed.Entries.Select(entry => (entry.Model, entry.Input, entry.CachedInput, entry.Output))
            );
    }

    private static string Mutate(string document, string mutation) =>
        mutation switch
        {
            "missing-footnote" => document.Replace(
                "requests whose prompt reaches the listed token threshold are billed at the higher rate",
                "requests are billed at the rate we feel like",
                StringComparison.Ordinal
            ),
            "missing-heading" => document.Replace("Text API Pricing", "Text Rates", StringComparison.Ordinal),
            "renamed-column" => document.Replace(
                "| Input / 1M tokens |",
                "| Input / 1K tokens |",
                StringComparison.Ordinal
            ),
            "reordered-columns" => document.Replace(
                "| Model | Context | Input / 1M tokens | Cached input / 1M tokens | Output / 1M tokens |",
                "| Model | Context | Output / 1M tokens | Cached input / 1M tokens | Input / 1M tokens |",
                StringComparison.Ordinal
            ),
            "single-lane" => document.Replace(
                "| grok-4.6 (≥ 200k prompt tokens) | 500k | $4.00 | $1.00 | $12.00 |\n",
                string.Empty,
                StringComparison.Ordinal
            ),
            "duplicate-lane" => document.Replace(
                "| grok-4.5 (< 200k prompt tokens) | 500k | $2.00 | $0.30 | $6.00 |",
                "| grok-4.6 (< 200k prompt tokens) | 500k | $2.00 | $0.30 | $6.00 |",
                StringComparison.Ordinal
            ),
            "two-thresholds" => document.Replace(
                "| grok-4.5 (< 200k prompt tokens) |",
                "| grok-4.5 (< 300k prompt tokens) |",
                StringComparison.Ordinal
            ),
            "unparseable-rate" => document.Replace(
                "| grok-4.6 (< 200k prompt tokens) | 500k | $2.00 |",
                "| grok-4.6 (< 200k prompt tokens) | 500k | on request |",
                StringComparison.Ordinal
            ),
            "inverted-lanes" => document.Replace(
                "| grok-4.6 (≥ 200k prompt tokens) | 500k | $4.00 | $1.00 | $12.00 |",
                "| grok-4.6 (≥ 200k prompt tokens) | 500k | $1.00 | $0.25 | $3.00 |",
                StringComparison.Ordinal
            ),
            "short-row" => document.Replace(
                "| grok-4.6 (< 200k prompt tokens) | 500k | $2.00 | $0.50 | $6.00 |",
                "| grok-4.6 (< 200k prompt tokens) | 500k | $2.00 |",
                StringComparison.Ordinal
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };

    private static string Fixture() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Pricing", "Fixtures", "xai-pricing.md"));

    private sealed class StubHandler(string document) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(document, System.Text.Encoding.UTF8, "text/markdown"),
                }
            );
    }
}
