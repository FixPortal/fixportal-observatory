using AiObservatory.Data.Pricing;
using AiObservatory.Data.Pricing.Catalogs;
using AwesomeAssertions;
using NodaTime;

namespace AiObservatory.Data.Tests.Pricing;

public sealed class AnthropicPriceCatalogTests
{
    private const string Lower = "prompt_le_100000";
    private const string Upper = "prompt_gt_100000";
    private static readonly LocalDate Start = new(2026, 10, 22);

    [Fact]
    public void CompletePairRequiresExactContextAndPreservesDifferentKeys()
    {
        var lower = Entry(Lower);
        var upper = Entry(Upper) with { Input = 3m, Output = 33m };
        var catalog = Catalog(lower, upper);
        ((Action)catalog.Validate).Should().NotThrow();
        catalog.RequiresPromptContext("claude-haiku-5-5", Start).Should().BeTrue();
        catalog.Resolve("claude-haiku-5-5", Start).Should().BeNull();
        catalog.Resolve("claude-haiku-5-5", Start, "unknown").Should().BeNull();
        catalog.Resolve("claude-haiku-5-5", Start, Lower).Should().BeSameAs(lower);
        catalog.Resolve("claude-haiku-5-5", Start, Upper).Should().BeSameAs(upper);
        PricingEvidence.RateKey(lower).Should().NotBe(PricingEvidence.RateKey(upper));
        PricingCatalogJson
            .Deserialize<AnthropicPriceCatalog>(PricingCatalogJson.Serialize(catalog))
            .Resolve("claude-haiku-5-5", Start, Upper)!
            .Input.Should()
            .Be(3m);
    }

    [Fact]
    public void DuplicateBandFails()
    {
        var act = () => Catalog(Entry(Lower), Entry(Lower), Entry(Upper)).Validate();
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void IncompletePairFails()
    {
        var act = () => Catalog(Entry(Lower)).Validate();
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void FlatAndBandedSameWindowFail()
    {
        var act = () => Catalog(Entry(null), Entry(Lower), Entry(Upper)).Validate();
        act.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData("prompt_le_200000")]
    [InlineData("PROMPT_LE_100000")]
    [InlineData("")]
    public void UnknownThresholdQualifierFails(string context)
    {
        var act = () => Catalog(Entry(context)).Validate();
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void UnsupportedModelCannotAcquirePromptBands()
    {
        var act = () =>
            Catalog(
                    Entry(Lower) with
                    {
                        ModelPrefix = "claude-haiku-6",
                        Aliases = ["claude-haiku-6"],
                    },
                    Entry(Upper) with
                    {
                        ModelPrefix = "claude-haiku-6",
                        Aliases = ["claude-haiku-6"],
                    }
                )
                .Validate();
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void HistoricalFlatThenBandedWindowResolves()
    {
        var flat = Entry(null) with { EffectiveFrom = Start.PlusDays(-30) };
        var lower = Entry(Lower);
        var upper = Entry(Upper);
        var catalog = Catalog(flat, lower, upper);
        ((Action)catalog.Validate).Should().NotThrow();
        catalog.RequiresPromptContext("claude-haiku-5-5", Start.PlusDays(-1)).Should().BeFalse();
        catalog.Resolve("claude-haiku-5-5", Start.PlusDays(-1)).Should().BeSameAs(flat);
        catalog.RequiresPromptContext("claude-haiku-5-5", Start).Should().BeTrue();
        catalog.Resolve("claude-haiku-5-5", Start).Should().BeNull();
        catalog.Resolve("claude-haiku-5-5", Start, Lower).Should().BeSameAs(lower);
        catalog.Resolve("claude-haiku-5-5", Start, Upper).Should().BeSameAs(upper);
    }

    [Theory]
    [InlineData("claude-haiku-5-5-20261001")]
    [InlineData("haiku-retained-alias")]
    public void OlderExactAliasCannotBypassNewBandedEra(string alias)
    {
        var flat = Entry(null) with { EffectiveFrom = new LocalDate(2026, 10, 1), Aliases = [alias] };
        var lower = Entry(Lower) with { Aliases = ["claude-haiku-5-5"] };
        var upper = Entry(Upper) with { Aliases = ["claude-haiku-5-5"] };
        var catalog = Catalog(flat, lower, upper);
        ((Action)catalog.Validate).Should().NotThrow();
        catalog.Resolve(alias, Start.PlusDays(-1)).Should().BeSameAs(flat);
        catalog.RequiresPromptContext(alias, Start.PlusDays(-1)).Should().BeFalse();
        catalog.RequiresPromptContext(alias, Start.PlusDays(1)).Should().BeTrue();
        catalog.Resolve(alias, Start.PlusDays(1)).Should().BeNull();
        catalog.Resolve(alias, Start.PlusDays(1), "unknown").Should().BeNull();
        catalog.Resolve(alias, Start.PlusDays(1), Lower).Should().BeSameAs(lower);
        catalog.Resolve(alias, Start.PlusDays(1), Upper).Should().BeSameAs(upper);
    }

    [Fact]
    public void DistinctCanonicalDatedModelKeepsItsOwnEra()
    {
        const string datedModel = "claude-haiku-5-5-20261001";
        var distinct = Entry(null) with
        {
            ModelPrefix = datedModel,
            Aliases = [datedModel],
            EffectiveFrom = new LocalDate(2026, 10, 1),
        };
        var catalog = Catalog(distinct, Entry(Lower), Entry(Upper));
        ((Action)catalog.Validate).Should().NotThrow();
        catalog.Resolve(datedModel, Start.PlusDays(1)).Should().BeSameAs(distinct);
        catalog.RequiresPromptContext(datedModel, Start.PlusDays(1)).Should().BeFalse();
        catalog.RequiresPromptContext("claude-haiku-5-5", Start.PlusDays(1)).Should().BeTrue();
    }

    [Fact]
    public void CompetingExplicitAliasesDoNotPickAnArbitraryFamily()
    {
        var first = Entry(null) with { Aliases = ["shared-alias"] };
        var second = Entry(null) with
        {
            ModelPrefix = "claude-sonnet-5",
            Aliases = ["shared-alias"],
            EffectiveFrom = Start.PlusDays(1),
        };
        var catalog = Catalog(first, second);
        ((Action)catalog.Validate).Should().NotThrow();
        catalog.Resolve("shared-alias", Start.PlusDays(2)).Should().BeNull();
        catalog.Resolve("claude-haiku-5-5", Start.PlusDays(2)).Should().BeSameAs(first);
        catalog.Resolve("claude-sonnet-5", Start.PlusDays(2)).Should().BeSameAs(second);
    }

    [Fact]
    public void BandedThenFlatEraDoesNotRetainContextRequirement()
    {
        var flat = Entry(null) with { EffectiveFrom = Start.PlusDays(1) };
        var catalog = Catalog(Entry(Lower), Entry(Upper), flat);
        ((Action)catalog.Validate).Should().NotThrow();
        catalog.RequiresPromptContext("claude-haiku-5-5", Start.PlusDays(1)).Should().BeFalse();
        catalog.Resolve("claude-haiku-5-5", Start.PlusDays(1)).Should().BeSameAs(flat);
    }

    [Theory]
    [InlineData("claude-haiku-5-5", true)]
    [InlineData("CLAUDE-HAIKU-5-5", true)]
    [InlineData("claude-haiku-5-5-20261022", true)]
    [InlineData("haiku-qualified", true)]
    [InlineData("claude-haiku-5-50", false)]
    [InlineData("claude-haiku-5-5-extra", false)]
    [InlineData("claude-haiku-5-5-2026-10-22", false)]
    [InlineData("claude-haiku-5-5-20260230", false)]
    [InlineData("claude-haiku-5-5-20261022-extra", false)]
    public void ExactExplicitAndClaudeDatedAliasesOnly(string model, bool matches)
    {
        var catalog = Catalog(Entry(Lower), Entry(Upper));
        catalog.RequiresPromptContext(model, Start).Should().Be(matches);
        (catalog.Resolve(model, Start, Lower) is not null).Should().Be(matches);
    }

    [Fact]
    public void ProviderDeclaredStartGatesButObservedStartRemainsAssumed()
    {
        var declared = Catalog(Entry(Lower), Entry(Upper));
        declared.Resolve("claude-haiku-5-5", Start.PlusDays(-1), Lower).Should().BeNull();
        declared.RequiresPromptContext("claude-haiku-5-5", Start.PlusDays(-1)).Should().BeFalse();
        var observed = Catalog(
            Entry(Lower) with
            {
                EffectiveDateIsProviderDeclared = false,
            },
            Entry(Upper) with
            {
                EffectiveDateIsProviderDeclared = false,
            }
        );
        observed.RequiresPromptContext("claude-haiku-5-5", Start.PlusDays(-1)).Should().BeTrue();
        observed.Resolve("claude-haiku-5-5", Start.PlusDays(-1), Upper)!.Context.Should().Be(Upper);
    }

    [Fact]
    public void PairMustShareWindowDeclarationAndAliases()
    {
        var declaration = () =>
            Catalog(Entry(Lower), Entry(Upper) with { EffectiveDateIsProviderDeclared = false }).Validate();
        declaration.Should().Throw<InvalidDataException>();
        var aliases = () => Catalog(Entry(Lower), Entry(Upper) with { Aliases = ["claude-haiku-5-5"] }).Validate();
        aliases.Should().Throw<InvalidDataException>();
    }

    private static AnthropicPriceCatalog Catalog(params AnthropicPriceEntry[] entries) =>
        new("USD", "https://example.test/pricing", Instant.FromUtc(2026, 10, 22, 12, 0), entries);

    private static AnthropicPriceEntry Entry(string? context) =>
        new(
            "claude-haiku-5-5",
            ["claude-haiku-5-5", "haiku-qualified"],
            Start,
            true,
            1m,
            11m,
            0.1m,
            1.25m,
            2m,
            0.4m,
            4.4m,
            null,
            null,
            1.1m
        )
        {
            Context = context,
        };
}
