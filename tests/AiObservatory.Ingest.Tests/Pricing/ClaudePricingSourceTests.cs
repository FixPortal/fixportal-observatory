using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    [Fact]
    public void TwoBandsPreserveModelAndExactJoins()
    {
        var catalog = ClaudePricingSource.Parse(ReadFixture("claude-context-pricing.md"), RetrievedAt);
        var lanes = catalog.Entries.Where(entry => entry.ModelPrefix == "claude-haiku-5-5").ToList();
        lanes.Should().HaveCount(2);
        lanes.Select(entry => entry.Context).Should().BeEquivalentTo("prompt_le_100000", "prompt_gt_100000");
        foreach (var entry in lanes)
        {
            entry.Aliases.Should().Equal("claude-haiku-5-5");
            using var json = JsonDocument.Parse(PricingCatalogJson.Serialize(entry));
            var context = json.RootElement.GetProperty("context").GetString();
            if (context == "prompt_le_100000")
            {
                entry.Input.Should().Be(1m);
                entry.Output.Should().Be(11m);
                entry.CacheRead.Should().Be(0.1m);
                entry.CacheWrite5m.Should().Be(1.25m);
                entry.CacheWrite1h.Should().Be(2m);
                entry.BatchInput.Should().Be(0.4m);
                entry.BatchOutput.Should().Be(4.4m);
                entry.FastInput.Should().BeNull();
                entry.FastOutput.Should().BeNull();
            }
            else
            {
                context.Should().Be("prompt_gt_100000");
                entry.Input.Should().Be(3m);
                entry.Output.Should().Be(33m);
                entry.CacheRead.Should().Be(0.3m);
                entry.CacheWrite5m.Should().Be(3.75m);
                entry.CacheWrite1h.Should().Be(6m);
                entry.BatchInput.Should().Be(0.9m);
                entry.BatchOutput.Should().Be(9.9m);
                entry.FastInput.Should().Be(7m);
                entry.FastOutput.Should().Be(77m);
            }
            entry.UsInferenceMultiplier.Should().Be(1.1m);
        }
    }

    [Fact]
    public void ParserAcceptsExplicitUsdUnitsObservedInFastPricing()
    {
        var document = ReadFixture("claude-context-pricing.md")
            .Replace(" / MTok", " USD / MTok", StringComparison.Ordinal);
        var catalog = ClaudePricingSource.Parse(document, RetrievedAt);
        var upper = catalog.Resolve("claude-haiku-5-5", ObservedOn, "prompt_gt_100000");
        upper!.Input.Should().Be(3m);
        upper.FastInput.Should().Be(7m);
        upper.FastOutput.Should().Be(77m);
    }

    [Theory]
    [InlineData(" EUR / MTok")]
    [InlineData(" USDX / MTok")]
    [InlineData(" USD / KTok")]
    public void ExplicitCurrencyAndUnitStillFailClosed(string unit)
    {
        var document = ReadFixture("claude-context-pricing.md")
            .Replace("$77 / MTok", "$77" + unit, StringComparison.Ordinal);
        var act = () => ClaudePricingSource.Parse(document, RetrievedAt);
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void ParserKeepsRetrievedDateAsObservationForEveryLane()
    {
        var catalog = ClaudePricingSource.Parse(ReadFixture("claude-context-pricing.md"), RetrievedAt);
        catalog.RetrievedAt.Should().Be(RetrievedAt);
        catalog
            .Entries.Should()
            .OnlyContain(entry => entry.EffectiveFrom == ObservedOn && !entry.EffectiveDateIsProviderDeclared);
        catalog.Entries.Should().OnlyContain(entry => entry.BatchInput > 0 && entry.BatchOutput > 0);
        var lanes = catalog.Entries.Where(entry => entry.ModelPrefix == "claude-haiku-5-5").ToList();
        lanes.Select(entry => entry.Context).Should().BeEquivalentTo("prompt_le_100000", "prompt_gt_100000");
        catalog.Resolve("claude-haiku-5-5", ObservedOn).Should().BeNull();
        catalog.Resolve("claude-haiku-5-5", ObservedOn, "prompt_le_100000").Should().NotBeNull();
        catalog.Resolve("claude-haiku-5-5", ObservedOn, "prompt_gt_100000").Should().NotBeNull();
        TestContext.Current.TestOutputHelper!.WriteLine(
            $"Parsed entries={catalog.Entries.Count}; banded={lanes.Count}; normalizedSha256={Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(PricingCatalogJson.Serialize(catalog))))}"
        );
    }

    [Theory]
    [InlineData("unit")]
    [InlineData("cache-multiplier")]
    [InlineData("geography")]
    [InlineData("duplicate-batch")]
    [InlineData("duplicate-fast")]
    public void BandedDocumentRetainsRateCacheGeographyAndModifierGuards(string mutation)
    {
        var document = ReadFixture("claude-context-pricing.md");
        const string batch = "| Claude Haiku 5.5 (for prompts over 100,000 tokens) | $0.9 / MTok | $9.9 / MTok |";
        const string fast = "| Claude Haiku 5.5 (for prompts over 100,000 tokens) | $7 / MTok | $77 / MTok |";
        document = mutation switch
        {
            "unit" => document.Replace("$33 / MTok", "$33 / KTok", StringComparison.Ordinal),
            "cache-multiplier" => document.Replace(
                "1.25x base input price",
                "1.5x base input price",
                StringComparison.Ordinal
            ),
            "geography" => document.Replace(
                "incurs a 1.1x multiplier",
                "incurs a 1.2x multiplier",
                StringComparison.Ordinal
            ),
            "duplicate-batch" => document.Replace(batch, $"{batch}\n{batch}", StringComparison.Ordinal),
            "duplicate-fast" => document.Replace(fast, $"{fast}\n{fast}", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        var act = () => ClaudePricingSource.Parse(document, RetrievedAt);
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void LegacyCatalogBytesAndKeysUnchanged()
    {
        var catalog = ClaudePricingSource.Parse(Fixture(), RetrievedAt);
        var entry = catalog.Resolve("claude-opus-5-20260801", ObservedOn)!;
        var usage = new UsageEvent
        {
            Provider = Provider.Anthropic,
            Model = "claude-opus-5-20260801",
            OccurredAt = RetrievedAt,
            InputTokens = 123,
            OutputTokens = 45,
            CacheReadTokens = 67,
            CacheWriteTokens = 89,
            CacheWrite1hTokens = 10,
            CostBasis = CostBasis.ListPriceEstimate,
            RawPayload = "{}",
        };
        PricingCatalogJson
            .Serialize(entry)
            .Should()
            .Be(
                """{"modelPrefix":"claude-opus-5","aliases":["claude-opus-5"],"effectiveFrom":"2026-08-24","effectiveDateIsProviderDeclared":false,"input":5,"output":25,"cacheRead":0.50,"cacheWrite5m":6.25,"cacheWrite1h":10,"batchInput":2.50,"batchOutput":12.50,"fastInput":10,"fastOutput":50,"usInferenceMultiplier":1.1}"""
            );
        Convert
            .ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(PricingCatalogJson.Serialize(catalog))))
            .Should()
            .Be("57e8eab0d379347862f600895ecc9003846d47ad15a62f5fdb54b01a1d64656f");
        PricingEvidence.RateKey(entry).Should().Be("898764ce6b58de6fcbb77d58c2030e9b868fa9e39f53805209e3bc8b7b16584e");
        PricingEvidence
            .RevisionDigest(usage)
            .Should()
            .Be("7480b5d57289a678d4633ea3fe75e2df55e3f540cd53c986925613cd3270afd1");
    }

    [Theory]
    [InlineData("duplicate-band")]
    [InlineData("incomplete-pair")]
    [InlineData("incomplete-batch")]
    [InlineData("flat-and-banded")]
    [InlineData("unknown-threshold")]
    [InlineData("unknown-wording")]
    [InlineData("unsupported-model")]
    [InlineData("unknown-fast-threshold")]
    public void ParserRejectsAmbiguousPromptLanes(string mutation)
    {
        var document = ReadFixture("claude-context-pricing.md");
        const string lower =
            "| Claude Haiku 5.5 (for prompts up to 100,000 tokens) | $1 / MTok | $1.25 / MTok | $2 / MTok | $0.1 / MTok | $11 / MTok |";
        document = mutation switch
        {
            "duplicate-band" => document.Replace(lower, $"{lower}\n{lower}", StringComparison.Ordinal),
            "incomplete-pair" => string.Join(
                '\n',
                document.Split('\n').Where(line => !line.Contains("for prompts over", StringComparison.Ordinal))
            ),
            "incomplete-batch" => document.Replace(
                "| Claude Haiku 5.5 (for prompts over 100,000 tokens) | $0.9 / MTok | $9.9 / MTok |",
                "",
                StringComparison.Ordinal
            ),
            "flat-and-banded" => document.Replace(
                lower,
                $"{lower}\n{lower.Replace(" (for prompts up to 100,000 tokens)", "", StringComparison.Ordinal)}",
                StringComparison.Ordinal
            ),
            "unknown-threshold" => document.Replace("100,000", "200,000", StringComparison.Ordinal),
            "unknown-wording" => document.Replace(
                "for prompts up to",
                "for prompt lengths under",
                StringComparison.Ordinal
            ),
            "unsupported-model" => document.Replace("Claude Haiku 5.5", "Claude Haiku 6", StringComparison.Ordinal),
            "unknown-fast-threshold" => document.Replace(
                "| Claude Haiku 5.5 (for prompts over 100,000 tokens) | $7",
                "| Claude Haiku 5.5 (for prompts over 200,000 tokens) | $7",
                StringComparison.Ordinal
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        var act = () => ClaudePricingSource.Parse(document, RetrievedAt);
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void UnknownThresholdQualifierFailsEvenWithoutDuplicateRows()
    {
        var document = Fixture()
            .Replace("Claude Sonnet 5", "Claude Sonnet 5 (for prompts up to 200,000 tokens)", StringComparison.Ordinal);
        var act = () => ClaudePricingSource.Parse(document, RetrievedAt);
        act.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData(" (for input lengths up to 200,000 tokens)")]
    [InlineData(" (over 200,000 tokens)")]
    [InlineData(" (for requests below 100k)")]
    public void ParserRejectsUnknownPricingQualifiersWithoutPrompt(string qualifier)
    {
        var document = Fixture().Replace("Claude Sonnet 5", "Claude Sonnet 5" + qualifier, StringComparison.Ordinal);
        var act = () => ClaudePricingSource.Parse(document, RetrievedAt);
        act.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData(" ([limited availability](https://anthropic.com/glasswing))")]
    [InlineData(" ([limited availability](https://support.claude.com/en/articles/14604842))")]
    [InlineData(
        " ([retired, except on Bedrock and Google Cloud](https://platform.claude.com/docs/en/about-claude/model-deprecations))"
    )]
    [InlineData(
        " ([retired, except on Google Cloud](https://platform.claude.com/docs/en/about-claude/model-deprecations))"
    )]
    public void ParserPreservesKnownNonPricingAnnotations(string qualifier)
    {
        var document = Fixture().Replace("Claude Sonnet 5", "Claude Sonnet 5" + qualifier, StringComparison.Ordinal);
        var catalog = ClaudePricingSource.Parse(document, RetrievedAt);
        var sonnet = catalog.Resolve("claude-sonnet-5", ObservedOn);
        sonnet!.Input.Should().Be(2m);
        sonnet.Output.Should().Be(10m);
        sonnet.BatchInput.Should().Be(1m);
        sonnet.BatchOutput.Should().Be(5m);
        sonnet.Context.Should().BeNull();
    }

    [Theory]
    [InlineData(
        " ([retired, except on Bedrock and Claude Models on Google Cloud (Google managed)](https://platform.claude.com/docs/en/about-claude/model-deprecations))"
    )]
    [InlineData(
        " ([retired, except on Claude Models on Google Cloud (Google managed)](https://platform.claude.com/docs/en/about-claude/model-deprecations))"
    )]
    public void ObservedGoogleManagedRetirementAnnotationsPreserveEveryLane(string qualifier)
    {
        var original = ReadFixture("claude-context-pricing.md");
        var document = original.Replace("Claude Sonnet 5", "Claude Sonnet 5" + qualifier, StringComparison.Ordinal);
        document.Should().NotBe(original);
        var expected = ClaudePricingSource.Parse(original, RetrievedAt);
        var actual = ClaudePricingSource.Parse(document, RetrievedAt);
        PricingCatalogJson.Serialize(actual).Should().Be(PricingCatalogJson.Serialize(expected));
        actual
            .Entries.Where(entry => entry.ModelPrefix == "claude-haiku-5-5")
            .Select(entry => entry.Context)
            .Should()
            .BeEquivalentTo("prompt_le_100000", "prompt_gt_100000");
        actual
            .Entries.Where(entry => entry.ModelPrefix == "claude-sonnet-5")
            .Should()
            .OnlyContain(entry => entry.Context == null);
    }

    [Theory]
    [InlineData(
        " ([Retired, except on Claude Models on Google Cloud (Google managed)](https://platform.claude.com/docs/en/about-claude/model-deprecations))"
    )]
    [InlineData(
        " ([retired, except on Claude Models on Google Cloud (Google Managed)](https://platform.claude.com/docs/en/about-claude/model-deprecations))"
    )]
    [InlineData(
        " ([retired, except on Claude Models on Google Cloud (Google managed)](https://example.invalid/model-deprecations))"
    )]
    [InlineData(
        " ([retired, except on Claude Models on Google Cloud (Google managed)](http://platform.claude.com/docs/en/about-claude/model-deprecations))"
    )]
    [InlineData(
        " ([retired, except on Claude Models on Google Cloud (Google managed); for prompts over 100,000 tokens](https://platform.claude.com/docs/en/about-claude/model-deprecations))"
    )]
    [InlineData(
        " ([retired, except on Claude Models on Google Cloud (Google managed)](https://platform.claude.com/docs/en/about-claude/model-deprecations)) (for prompts over 100,000 tokens)"
    )]
    [InlineData(
        " ([retired, except on Claude Models on Google Cloud (Google managed)](https://platform.claude.com/docs/en/about-claude/model-deprecations)) (for prompts over 200,000 tokens)"
    )]
    public void GoogleManagedPresentationAdmissionDoesNotEraseUnknownConditions(string qualifier)
    {
        var document = Fixture().Replace("Claude Sonnet 5", "Claude Sonnet 5" + qualifier, StringComparison.Ordinal);
        var act = () => ClaudePricingSource.Parse(document, RetrievedAt);
        act.Should()
            .Throw<InvalidDataException>()
            .WithMessage("Claude pricing contains an unsupported model qualifier.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("\t")]
    [InlineData("  ")]
    public void GoogleManagedPresentationAnnotationsKeepExactBoundary(string boundary)
    {
        const string qualifier =
            "([retired, except on Claude Models on Google Cloud (Google managed)](https://platform.claude.com/docs/en/about-claude/model-deprecations))";
        var document = Fixture()
            .Replace("Claude Sonnet 5", "Claude Sonnet 5" + boundary + qualifier, StringComparison.Ordinal);
        var act = () => ClaudePricingSource.Parse(document, RetrievedAt);
        act.Should()
            .Throw<InvalidDataException>()
            .WithMessage("Claude pricing contains a malformed model qualifier boundary.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("\t")]
    [InlineData("\u00a0")]
    [InlineData("  ")]
    [InlineData(" \t")]
    [InlineData("\t ")]
    public void ParserRejectsMalformedQualifierBoundaries(string boundary)
    {
        var document = ReadFixture("claude-context-pricing.md")
            .Replace(" (for prompts ", boundary + "(for prompts ", StringComparison.Ordinal);
        var act = () => ClaudePricingSource.Parse(document, RetrievedAt);
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void ParserRejectsPromptConditionTextOutsideARecognizedQualifier()
    {
        var document = ReadFixture("claude-context-pricing.md")
            .Replace(" (for prompts ", " for prompts ", StringComparison.Ordinal);
        var act = () => ClaudePricingSource.Parse(document, RetrievedAt);
        act.Should().Throw<InvalidDataException>();
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

    // The document Anthropic actually served on 2026-09-19, captured verbatim.
    //
    // Every other test in this class runs against claude-pricing.md, a snapshot taken when
    // the parser was written. That made upstream drift undetectable: this suite stayed green
    // from 2026-09-01 to 2026-09-19 while the live source failed 56 times in a row, because
    // the only document CI ever parsed was one that could not change. Two presentation edits
    // were enough to break it -- Title Case headings became sentence case with "&" spelled
    // "and", and two of the four tables gained markdown alignment colons in their separator
    // rows. No column, order or rate changed.
    //
    // So this fixture is not a duplicate of the other one. It pins the parser to a document
    // that was really observed rather than one chosen to suit it.
    [Fact]
    public void ParserAcceptsTheLiveDocumentStylingObservedOn20260919()
    {
        var catalog = ClaudePricingSource.Parse(LiveFixture(), RetrievedAt);

        var opus = catalog.Resolve("claude-opus-5-20260801", ObservedOn);

        opus.Should().NotBeNull();
        opus.Input.Should().Be(5m);
        opus.Output.Should().Be(25m);
        opus.CacheRead.Should().Be(0.50m);
        opus.CacheWrite5m.Should().Be(6.25m);
        opus.CacheWrite1h.Should().Be(10m);
    }

    // Tolerating presentation must not decay into tolerating anything. A column that is
    // dropped, reordered or replaced still has to fail loudly -- otherwise the fix above
    // trades a noisy break for a silent misread, which is strictly worse than the outage it
    // resolves.
    [Theory]
    [InlineData("| Model | Base input tokens | 5m cache writes | 1h cache writes | Cache hits and refreshes |")]
    [InlineData(
        "| Model | 5m cache writes | Base input tokens | 1h cache writes | Cache hits and refreshes | Output tokens |"
    )]
    [InlineData(
        "| Model | Base input tokens | 5m cache writes | 1h cache writes | Cache hits and refreshes | Thinking tokens |"
    )]
    public void ParserStillRejectsAStructuralHeaderChange(string replacementHeader)
    {
        var original = LiveFixture();
        var header = original
            .Split('\n')
            .Single(line =>
                line.TrimStart().StartsWith("| Model", StringComparison.Ordinal)
                && line.Contains("Base input tokens", StringComparison.Ordinal)
            );
        var document = original.Replace(header, replacementHeader, StringComparison.Ordinal);
        document.Should().NotBe(original, "the header row must actually have been replaced");

        var act = () => ClaudePricingSource.Parse(document, RetrievedAt);

        act.Should().Throw<InvalidDataException>();
    }

    private static string Fixture() => ReadFixture("claude-pricing.md");

    private static string LiveFixture() => ReadFixture("claude-pricing-live-2026-09-19.md");

    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Pricing", "Fixtures", name));

    private static string Bundle(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Pricing", "Bundled", name));
}
