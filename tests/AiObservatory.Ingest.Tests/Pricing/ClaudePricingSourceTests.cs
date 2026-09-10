using System.Net;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing;
using AiObservatory.Data.Pricing.Catalogs;
using AiObservatory.Ingest.Pricing;
using AiObservatory.Ingest.Sources;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NodaTime.Testing;

namespace AiObservatory.Ingest.Tests.Pricing;

public sealed class ClaudePricingSourceTests
{
    private static readonly Instant RetrievedAt = Instant.FromUtc(2026, 8, 24, 12, 0);
    private static readonly LocalDate ObservedOn = new(2026, 8, 24);

    [Fact]
    public void ParserPreservesCacheDurationsBatchFastAndStatedGeography()
    {
        var catalog = ClaudePricingSource.Parse(Fixture(), RetrievedAt);

        var opus = catalog.Resolve("claude-opus-5-20260801", ObservedOn);
        var older = catalog.Resolve("claude-opus-4-5-20251101", ObservedOn);

        opus.Should().NotBeNull();
        opus.Input.Should().Be(5m);
        opus.Output.Should().Be(25m);
        opus.CacheRead.Should().Be(0.50m);
        opus.CacheWrite5m.Should().Be(6.25m);
        opus.CacheWrite1h.Should().Be(10m);
        opus.BatchInput.Should().Be(2.50m);
        opus.BatchOutput.Should().Be(12.50m);
        opus.FastInput.Should().Be(10m);
        opus.FastOutput.Should().Be(50m);
        opus.UsInferenceMultiplier.Should().Be(1.1m);
        older!.FastInput.Should().BeNull();
        older.FastOutput.Should().BeNull();
        older.UsInferenceMultiplier.Should().BeNull();
    }

    [Fact]
    public void ParserKeepsSonnetFiveAtItsDeclaredCurrentStandardPriceWithoutInventingAnIncrease()
    {
        var catalog = ClaudePricingSource.Parse(Fixture(), RetrievedAt);

        var sonnetEntries = catalog.Entries.Where(entry => entry.ModelPrefix == "claude-sonnet-5").ToList();

        sonnetEntries.Should().ContainSingle();
        sonnetEntries[0].Input.Should().Be(2m);
        sonnetEntries[0].Output.Should().Be(10m);
        sonnetEntries[0].EffectiveFrom.Should().Be(ObservedOn);
        sonnetEntries[0].EffectiveDateIsProviderDeclared.Should().BeFalse();
    }

    [Theory]
    [InlineData("Claude Opus 5")]
    [InlineData("Claude Opus 4.8")]
    [InlineData("Claude Opus 4.5")]
    [InlineData("Claude Sonnet 5")]
    public void ParserRejectsRemovalOfARequiredModelFromBaseAndRelatedTables(string model)
    {
        var document = RemoveModelEverywhere(Fixture(), model);

        var act = () => ClaudePricingSource.Parse(document, RetrievedAt);

        act.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData("missing-heading")]
    [InlineData("duplicate-key")]
    [InlineData("overlapping-normalized-model")]
    [InlineData("partial-batch")]
    [InlineData("non-usd")]
    [InlineData("zero-rate")]
    [InlineData("negative-rate")]
    [InlineData("unknown-column")]
    public void ParserRejectsMalformedOrAmbiguousCatalogs(string mutation)
    {
        var act = () => ClaudePricingSource.Parse(Mutate(Fixture(), mutation), RetrievedAt);

        act.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData(typeof(OpenAiPricingSource))]
    [InlineData(typeof(ClaudePricingSource))]
    [InlineData(typeof(KimiPricingSource))]
    [InlineData(typeof(GooglePricingSource))]
    public void PricingSourcesAreDisposableSoTheRefreshScopeReleasesTheirClients(Type sourceType)
    {
        // A2: every pricing source owns at least one HttpClient built outside
        // IHttpClientFactory; a scoped, non-disposable registration abandoned one handler
        // (several for Kimi) and its connection pool to the finalizer on every refresh pass.
        sourceType.Should().Implement<IDisposable>();
    }

    [Fact]
    public async Task DisposingTheRefreshScopeDisposesTheSourceClient()
    {
        // Mirrors Program.RegisterPricingSources: scoped sources, one fresh scope per pass.
        await using var provider = new ServiceCollection()
            .AddScoped(_ => new ClaudePricingSource(
                new FakeClock(RetrievedAt),
                new FirstPartyDocumentFetcherTests.RecordingHandler(
                    (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Fixture()) }
                )
            ))
            .BuildServiceProvider();
        var scope = provider.CreateAsyncScope();
        var source = scope.ServiceProvider.GetRequiredService<ClaudePricingSource>();

        await scope.DisposeAsync();

        var act = () => source.FetchAsync(TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task FetchProducesTheClaudeSourceContract()
    {
        var handler = new FirstPartyDocumentFetcherTests.RecordingHandler(
            (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Fixture()) }
        );
        var source = new ClaudePricingSource(new FakeClock(RetrievedAt), handler);

        var candidate = await source.FetchAsync(TestContext.Current.CancellationToken);

        candidate!.Provider.Should().Be(Provider.Anthropic);
        candidate.SourceId.Should().Be(PricingSourceIds.Claude);
        candidate.SourceUrl.Should().Be("https://platform.claude.com/docs/en/about-claude/pricing.md");
        var catalog = PricingCatalogJson.Deserialize<AnthropicPriceCatalog>(candidate.NormalizedCatalog);
        ((Action)catalog.Validate).Should().NotThrow();
    }

    [Fact]
    public void BundledCatalogHasNoExcludedSonnetIncrease()
    {
        var catalog = PricingCatalogJson.Deserialize<AnthropicPriceCatalog>(Bundle("claude.json"));

        ((Action)catalog.Validate).Should().NotThrow();
        catalog
            .Should()
            .BeEquivalentTo(ClaudePricingSource.Parse(Fixture(), RetrievedAt), options => options.WithStrictOrdering());
        catalog.SourceUrl.Should().Be("https://platform.claude.com/docs/en/about-claude/pricing.md");
        catalog.RetrievedAt.Should().Be(RetrievedAt);
        catalog
            .Entries.Where(entry => entry.ModelPrefix == "claude-sonnet-5")
            .Should()
            .ContainSingle()
            .Which.Input.Should()
            .Be(2m);
    }

    [Fact]
    public void ParserAcceptsTheDocumentWithoutTheExpiredNoIncreaseSentence()
    {
        // The "will not occur" sentence is time-limited marketing prose: once its date passes,
        // Anthropic can delete it while the Sonnet 5 rate table is unchanged, and the parse
        // must still succeed — the rate itself is read from the table, not the prose.
        var document = Fixture()
            .Replace(
                " The previously scheduled increase to $3/$15 per million input/output tokens on September 1, 2026 will not occur.",
                "",
                StringComparison.Ordinal
            );

        var act = () => ClaudePricingSource.Parse(document, RetrievedAt);

        act.Should().NotThrow();
    }

    private static string Mutate(string document, string mutation)
    {
        const string opusRow = "| Claude Opus 5 | $5 / MTok | $6.25 / MTok | $10 / MTok | $0.50 / MTok | $25 / MTok |";
        const string opusAliasRow =
            "| Claude Opus 5 ([current](https://example.test)) | $5 / MTok | $6.25 / MTok | $10 / MTok | $0.50 / MTok | $25 / MTok |";
        const string sonnetBatch = "| Claude Sonnet 5 | $1 / MTok | $5 / MTok |";
        return mutation switch
        {
            "missing-heading" => document.Replace("### Fast mode pricing", "### Fast rates", StringComparison.Ordinal),
            "duplicate-key" => document.Replace(opusRow, $"{opusRow}\n{opusRow}", StringComparison.Ordinal),
            "overlapping-normalized-model" => document.Replace(
                opusRow,
                $"{opusRow}\n{opusAliasRow}",
                StringComparison.Ordinal
            ),
            "partial-batch" => document.Replace(sonnetBatch, "", StringComparison.Ordinal),
            "non-usd" => document.Replace("All prices are in USD.", "All prices are in EUR.", StringComparison.Ordinal),
            "zero-rate" => document.Replace("$25 / MTok", "$0 / MTok", StringComparison.Ordinal),
            "negative-rate" => document.Replace("$25 / MTok", "$-25 / MTok", StringComparison.Ordinal),
            "unknown-column" => document.Replace(
                "| Model | Base Input Tokens |",
                "| Model | Currency | Base Input Tokens |",
                StringComparison.Ordinal
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
    }

    private static string RemoveModelEverywhere(string document, string model)
    {
        var lines = document.Split('\n').Where(line => !line.StartsWith($"| {model} |", StringComparison.Ordinal));
        var result = string.Join('\n', lines);
        return model switch
        {
            "Claude Opus 5" => result.Replace(
                "| Claude Opus 5 / Claude Opus 4.8 |",
                "| Claude Opus 4.8 |",
                StringComparison.Ordinal
            ),
            "Claude Opus 4.8" => result.Replace(
                "| Claude Opus 5 / Claude Opus 4.8 |",
                "| Claude Opus 5 |",
                StringComparison.Ordinal
            ),
            _ => result,
        };
    }

    private static string Fixture() => ReadFixture("claude-pricing.md");

    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Pricing", "Fixtures", name));

    private static string Bundle(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Pricing", "Bundled", name));
}
