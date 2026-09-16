using System.Net;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing;
using AiObservatory.Data.Pricing.Catalogs;
using AiObservatory.Ingest.Pricing;
using AwesomeAssertions;
using NodaTime;
using NodaTime.Testing;

namespace AiObservatory.Ingest.Tests.Pricing;

public sealed class MetaPricingSourceTests
{
    private const string CatalogUrl = "https://openrouter.ai/api/v1/models";
    private static readonly Instant RetrievedAt = Instant.FromUtc(2026, 9, 16, 12, 0);
    private static readonly LocalDate ObservedOn = new(2026, 9, 16);

    [Theory]
    [InlineData("meta/muse-spark-1.3", 1.25, 4.25, 0.15)]
    [InlineData("meta/muse-spark-1.3-contributor", 0.10, 0.20, 0.002)]
    [InlineData("meta/muse-spark-1.2", 1.25, 4.25, 0.15)]
    [InlineData("meta/muse-glimmer-30b", 0.35, 1.50, 0.04)]
    [InlineData("meta/muse-glimmer-30b:batch", 0.175, 0.75, 0.02)]
    public void ParserConvertsOpenRouterPerTokenRatesToPerMillion(
        string model,
        double input,
        double output,
        double cacheRead
    )
    {
        var (catalog, _) = MetaPricingSource.Parse(Fixture(), RetrievedAt);

        var entry = catalog.Resolve(model, ObservedOn);

        entry.Should().NotBeNull();
        entry.Input.Should().Be((decimal)input);
        entry.Output.Should().Be((decimal)output);
        entry.CacheRead.Should().Be((decimal)cacheRead);
        entry.EffectiveDateIsProviderDeclared.Should().BeFalse();
    }

    [Fact]
    public void ParserKeepsOnlyMetaModelsFromACatalogueThatCarriesEveryVendor()
    {
        var (catalog, _) = MetaPricingSource.Parse(Fixture(), RetrievedAt);

        catalog.Entries.Should().HaveCount(7);
        catalog.Entries.Should().AllSatisfy(entry => entry.Model.Should().StartWith("meta/"));
        catalog.Currency.Should().Be("USD");
        catalog.SourceUrl.Should().Be(CatalogUrl);
    }

    /// <summary>
    /// The evidence is the Meta rows alone, so a price move on an unrelated vendor cannot
    /// change the content hash and churn a Meta snapshot out of an otherwise identical catalogue.
    /// </summary>
    [Fact]
    public void EvidenceExcludesModelsFromOtherVendors()
    {
        var (_, evidence) = MetaPricingSource.Parse(Fixture(), RetrievedAt);

        evidence.Should().Contain("meta/muse-spark-1.3");
        evidence.Should().NotContain("\"id\": \"openai/");
        evidence.Should().NotContain("\"id\": \"anthropic/");
    }

    /// <summary>
    /// The contributor lane costs a twelfth of the standard one and `:batch` half of its parent,
    /// so a prefix match would misprice by an order of magnitude in either direction.
    /// </summary>
    [Theory]
    [InlineData("meta/muse-spark-1.3-preview")]
    [InlineData("meta/muse-spark")]
    [InlineData("muse-spark-1.3")]
    public void ResolveRefusesAModelThatOnlySharesAPrefixWithAPublishedOne(string model)
    {
        var (catalog, _) = MetaPricingSource.Parse(Fixture(), RetrievedAt);

        catalog.Resolve(model, ObservedOn).Should().BeNull();
    }

    [Theory]
    [InlineData("no-data")]
    [InlineData("no-meta")]
    [InlineData("missing-pricing")]
    [InlineData("missing-prompt-rate")]
    [InlineData("free-rate")]
    [InlineData("unparseable-rate")]
    [InlineData("cache-above-input")]
    public void ParserRejectsMalformedOrUnpriceableCatalogues(string mutation)
    {
        var document = Mutate(Fixture(), mutation);

        var act = () => MetaPricingSource.Parse(document, RetrievedAt);

        act.Should().Throw<Exception>().Which.Should().Match(exception => exception is InvalidDataException);
    }

    [Fact]
    public async Task FetchReturnsACandidateWhoseCatalogMatchesTheParsedDocument()
    {
        var document = Fixture();
        using var source = new MetaPricingSource(new FakeClock(RetrievedAt), new StubHandler(document));

        var candidate = await source.FetchAsync(TestContext.Current.CancellationToken);

        candidate.Should().NotBeNull();
        candidate.Provider.Should().Be(Provider.Meta);
        candidate.SourceId.Should().Be(PricingSourceIds.MetaOpenRouter);
        candidate.SourceUrl.Should().Be(CatalogUrl);
        candidate.RetrievedAt.Should().Be(RetrievedAt);
        PricingCatalogJson
            .Deserialize<MetaPriceCatalog>(candidate.NormalizedCatalog)
            .Should()
            .BeEquivalentTo(
                MetaPricingSource.Parse(document, RetrievedAt).Catalog,
                options => options.WithStrictOrdering()
            );
    }

    [Fact]
    public async Task FetchReturnsTheSameCandidateInstanceWhenTheCatalogueHasNotChanged()
    {
        using var source = new MetaPricingSource(new FakeClock(RetrievedAt), new StubHandler(Fixture()));

        var first = await source.FetchAsync(TestContext.Current.CancellationToken);
        var second = await source.FetchAsync(TestContext.Current.CancellationToken);

        second.Should().BeSameAs(first);
    }

    [Fact]
    public void BundledCatalogMatchesTheRatesOpenRouterPublishes()
    {
        var (parsed, _) = MetaPricingSource.Parse(Fixture(), RetrievedAt);
        var bundled = PricingCatalogJson.Deserialize<MetaPriceCatalog>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Pricing", "Bundled", "meta.json"))
        );

        bundled
            .Entries.Select(entry => (entry.Model, entry.Input, entry.Output, entry.CacheRead))
            .Should()
            .BeEquivalentTo(parsed.Entries.Select(entry => (entry.Model, entry.Input, entry.Output, entry.CacheRead)));
    }

    private static string Mutate(string document, string mutation) =>
        mutation switch
        {
            "no-data" => document.Replace("\"data\"", "\"models\"", StringComparison.Ordinal),
            "no-meta" => document.Replace("\"meta/", "\"nvidia/", StringComparison.Ordinal),
            "missing-pricing" => document.Replace("\"pricing\"", "\"rates\"", StringComparison.Ordinal),
            "missing-prompt-rate" => document.Replace("\"prompt\":", "\"input\":", StringComparison.Ordinal),
            "free-rate" => document.Replace("\"0.00000125\"", "\"0\"", StringComparison.Ordinal),
            "unparseable-rate" => document.Replace("\"0.00000125\"", "\"on request\"", StringComparison.Ordinal),
            "cache-above-input" => document.Replace("\"0.00000015\"", "\"0.000005\"", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };

    private static string Fixture() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Pricing", "Fixtures", "meta-openrouter.json"));

    private sealed class StubHandler(string document) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(document, System.Text.Encoding.UTF8, "application/json"),
                }
            );
    }
}
