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

public sealed class OpenAiPricingSourceTests
{
    private static readonly Instant RetrievedAt = Instant.FromUtc(2026, 8, 24, 12, 0);
    private static readonly LocalDate ObservedOn = new(2026, 8, 24);

    [Theory]
    [InlineData("standard", "short", 2.50, 0.25, 15.00)]
    [InlineData("standard", "long", 5.00, 0.50, 22.50)]
    [InlineData("batch", "short", 1.25, 0.13, 7.50)]
    [InlineData("flex", "long", 2.50, 0.25, 11.25)]
    [InlineData("fast", "short", 5.00, 0.50, 30.00)]
    public void ParserRetainsObservedLaneDimensions(
        string processing,
        string context,
        double input,
        double cachedInput,
        double output
    )
    {
        var catalog = OpenAiPricingSource.Parse(Fixture(), RetrievedAt);

        var entry = catalog.Resolve("gpt-5.4-2026-08-01", processing, context, "global", ObservedOn);

        entry.Should().NotBeNull();
        entry.Input.Should().Be((decimal)input);
        entry.CachedInput.Should().Be((decimal)cachedInput);
        entry.Output.Should().Be((decimal)output);
        entry.EffectiveFrom.Should().Be(ObservedOn);
        entry.EffectiveDateIsProviderDeclared.Should().BeFalse();
    }

    [Fact]
    public void ParserPreservesAnObservedCacheWriteRate()
    {
        var catalog = OpenAiPricingSource.Parse(Fixture(), RetrievedAt);

        catalog.Resolve("gpt-5.6-sol", "standard", "short", "global", ObservedOn)!.CacheWrite.Should().Be(5m);
        catalog.Resolve("gpt-5.4", "standard", "short", "global", ObservedOn)!.CacheWrite.Should().BeNull();
    }

    [Fact]
    public void ParserScopesTheRequiredUnitDeclarationToTheAcceptedPricingSection()
    {
        var document = Fixture() + "\n## Other product pricing\n\nPrices per 1M tokens.\n";

        var act = () => OpenAiPricingSource.Parse(document, RetrievedAt);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("missing-heading")]
    [InlineData("duplicate-key")]
    [InlineData("overlapping-normalized-model")]
    [InlineData("partial-table")]
    [InlineData("non-usd")]
    [InlineData("zero-rate")]
    [InlineData("negative-rate")]
    [InlineData("unknown-column")]
    [InlineData("missing-unit")]
    [InlineData("duplicate-unit")]
    [InlineData("conflicting-unit")]
    [InlineData("per-1k-unit")]
    public void ParserRejectsMalformedOrAmbiguousCatalogs(string mutation)
    {
        var document = Mutate(Fixture(), mutation);

        var act = () => OpenAiPricingSource.Parse(document, RetrievedAt);

        act.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData("standard", "gpt-5.6-sol")]
    [InlineData("standard", "gpt-5.4")]
    [InlineData("batch", "gpt-5.6-sol")]
    [InlineData("batch", "gpt-5.4")]
    [InlineData("flex", "gpt-5.6-sol")]
    [InlineData("flex", "gpt-5.4")]
    [InlineData("fast", "gpt-5.6-sol")]
    [InlineData("fast", "gpt-5.4")]
    public void ParserRejectsRemovalOfAnIndividualRequiredModelRow(string processing, string model)
    {
        var document = RemoveModelRow(Fixture(), processing, model);

        var act = () => OpenAiPricingSource.Parse(document, RetrievedAt);

        act.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData("standard", "gpt-5.4", "short")]
    [InlineData("batch", "gpt-5.4", "long")]
    [InlineData("flex", "gpt-5.6-sol", "short")]
    [InlineData("fast", "gpt-5.6-sol", "long")]
    public void ParserRejectsRemovalOfAnIndividualRequiredContextLane(string processing, string model, string context)
    {
        var document = RemoveContextLane(Fixture(), processing, model, context);

        var act = () => OpenAiPricingSource.Parse(document, RetrievedAt);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public async Task FetchBuildsAnExactEvidenceCandidateAndReusesItWhenUnchanged()
    {
        var raw = Fixture();
        var handler = new FirstPartyDocumentFetcherTests.RecordingHandler(
            (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(raw) }
        );
        var source = new OpenAiPricingSource(new FakeClock(RetrievedAt), handler);

        var first = await source.FetchAsync(TestContext.Current.CancellationToken);
        var second = await source.FetchAsync(TestContext.Current.CancellationToken);

        first.Should().BeSameAs(second);
        first.Provider.Should().Be(Provider.OpenAI);
        first.SourceId.Should().Be(PricingSourceIds.OpenAi);
        first.SourceUrl.Should().Be("https://developers.openai.com/api/docs/pricing.md");
        first.RawEvidence.Should().Be(raw);
        first.ContentHash.Should().Be(PricingSnapshotCandidate.ComputeContentHash(raw, first.NormalizedCatalog));
        var catalog = PricingCatalogJson.Deserialize<OpenAiPriceCatalog>(first.NormalizedCatalog);
        ((Action)catalog.Validate).Should().NotThrow();
    }

    [Fact]
    public void BundledCatalogUsesTheValidatedObservedFixture()
    {
        var catalog = PricingCatalogJson.Deserialize<OpenAiPriceCatalog>(Bundle("openai.json"));

        ((Action)catalog.Validate).Should().NotThrow();
        catalog
            .Should()
            .BeEquivalentTo(OpenAiPricingSource.Parse(Fixture(), RetrievedAt), options => options.WithStrictOrdering());
        catalog.SourceUrl.Should().Be("https://developers.openai.com/api/docs/pricing.md");
        catalog.RetrievedAt.Should().Be(RetrievedAt);
        catalog.Resolve("gpt-5.4", "standard", "short", "global", ObservedOn)!.Input.Should().Be(2.50m);
    }

    private static string Mutate(string document, string mutation) =>
        mutation switch
        {
            "missing-heading" => document.Replace(
                "### Batch pricing data",
                "### Batch rates",
                StringComparison.Ordinal
            ),
            "duplicate-key" => document.Replace(
                "| gpt-5.4 (<272K context length) | $2.50 | $0.25 | - | $15.00 | $5.00 | $0.50 | - | $22.50 |",
                "| gpt-5.4 (<272K context length) | $2.50 | $0.25 | - | $15.00 | $5.00 | $0.50 | - | $22.50 |\n| gpt-5.4 (<272K context length) | $2.50 | $0.25 | - | $15.00 | $5.00 | $0.50 | - | $22.50 |",
                StringComparison.Ordinal
            ),
            "overlapping-normalized-model" => document.Replace(
                "| gpt-5.4 (<272K context length) | $2.50 | $0.25 | - | $15.00 | $5.00 | $0.50 | - | $22.50 |",
                "| gpt-5.4 (<272K context length) | $2.50 | $0.25 | - | $15.00 | $5.00 | $0.50 | - | $22.50 |\n| gpt-5.4 | $2.50 | $0.25 | - | $15.00 | $5.00 | $0.50 | - | $22.50 |",
                StringComparison.Ordinal
            ),
            "partial-table" => document
                .Replace(
                    "| gpt-5.6-sol | $2.00 | $0.20 | $2.50 | $10.00 | $4.00 | $0.40 | $5.00 | $15.00 |",
                    "",
                    StringComparison.Ordinal
                )
                .Replace(
                    "| gpt-5.4 (<272K context length) | $1.25 | $0.13 | - | $7.50 | $2.50 | $0.25 | - | $11.25 |",
                    "",
                    StringComparison.Ordinal
                ),
            "non-usd" => document.Replace("$2.50", "€2.50", StringComparison.Ordinal),
            "zero-rate" => document.Replace("$15.00", "$0.00", StringComparison.Ordinal),
            "negative-rate" => document.Replace("$15.00", "$-15.00", StringComparison.Ordinal),
            "unknown-column" => document.Replace(
                "| Model | Short context input |",
                "| Model | Currency | Short context input |",
                StringComparison.Ordinal
            ),
            "missing-unit" => document.Replace("Prices per 1M tokens.", "", StringComparison.Ordinal),
            "duplicate-unit" => document.Replace(
                "Prices per 1M tokens.",
                "Prices per 1M tokens.\nPrices per 1M tokens.",
                StringComparison.Ordinal
            ),
            "conflicting-unit" => document.Replace(
                "Prices per 1M tokens.",
                "Prices per 1M tokens.\nPrices per 1K tokens.",
                StringComparison.Ordinal
            ),
            "per-1k-unit" => document.Replace(
                "Prices per 1M tokens.",
                "Prices per 1K tokens.",
                StringComparison.Ordinal
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };

    private static string RemoveModelRow(string document, string processing, string model) =>
        MutateTable(
            document,
            processing,
            lines => lines.Where(line => !line.StartsWith($"| {model}", StringComparison.Ordinal)).ToArray()
        );

    private static string RemoveContextLane(string document, string processing, string model, string context) =>
        MutateTable(
            document,
            processing,
            lines =>
                lines
                    .Select(line =>
                    {
                        if (!line.StartsWith($"| {model}", StringComparison.Ordinal))
                        {
                            return line;
                        }

                        var cells = line.Split('|');
                        var start = context == "short" ? 2 : 6;
                        for (var index = start; index < start + 4; index++)
                        {
                            cells[index] = " - ";
                        }

                        return string.Join('|', cells);
                    })
                    .ToArray()
        );

    private static string MutateTable(string document, string processing, Func<string[], string[]> mutation)
    {
        var heading = $"### {char.ToUpperInvariant(processing[0])}{processing[1..]} pricing data";
        var start = document.IndexOf(heading, StringComparison.Ordinal);
        var end = document.IndexOf(
            "\n\n",
            document.IndexOf("\n|", start, StringComparison.Ordinal) + 2,
            StringComparison.Ordinal
        );
        if (end < 0)
        {
            end = document.Length;
        }

        var table = document[start..end];
        var changed = string.Join('\n', mutation(table.Split('\n')));
        return document[..start] + changed + document[end..];
    }

    private static string Fixture() => ReadFixture("openai-pricing.md");

    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Pricing", "Fixtures", name));

    private static string Bundle(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Pricing", "Bundled", name));
}
