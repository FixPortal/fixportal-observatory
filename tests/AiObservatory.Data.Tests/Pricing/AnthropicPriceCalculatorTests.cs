using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing;
using AiObservatory.Data.Pricing.Catalogs;
using AwesomeAssertions;
using NodaTime;

namespace AiObservatory.Data.Tests.Pricing;

public sealed class AnthropicPriceCalculatorTests
{
    [Theory]
    [InlineData(CostBasis.Notional)]
    [InlineData(CostBasis.ListPriceEstimate)]
    public void BandedMissingContextNeverDefaultsEvenNotional(CostBasis basis)
    {
        var usage = ClaudeContextFixture.Event("{}");
        usage.CostBasis = basis;
        new AnthropicPriceCalculator().CalculateWithEvidence(usage, ClaudeContextFixture.Json()).Should().BeNull();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100000)]
    [InlineData(100001)]
    [InlineData(10000000)]
    public void DailyTotalsCannotCertifyBand(long input)
    {
        var usage = ClaudeContextFixture.Event(
            """{"model_context_window":1000000,"context_tokens":100001,"request_count":1}"""
        );
        usage.InputTokens = input;
        new AnthropicPriceCalculator().CalculateWithEvidence(usage, ClaudeContextFixture.Json()).Should().BeNull();
        PricingEvidence.SourceDimensions(usage).Context.Should().BeNull();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"context\":\"prompt_le_100000\"}")]
    [InlineData(
        "{\"claude_prompt_context_version\":\"2\",\"claude_prompt_context_basis\":\"deduplicated_message_group\",\"context\":\"prompt_le_100000\"}"
    )]
    [InlineData(
        "{\"claude_prompt_context_version\":1,\"claude_prompt_context_basis\":\"deduplicated_message_group\",\"context\":\"prompt_le_100000\"}"
    )]
    [InlineData(
        "{\"claude_prompt_context_version\":\"1\",\"claude_prompt_context_basis\":\"daily_total\",\"context\":\"prompt_le_100000\"}"
    )]
    [InlineData(
        "{\"claude_prompt_context_version\":\"1\",\"claude_prompt_context_basis\":\"deduplicated_message_group\",\"context\":\"PROMPT_LE_100000\"}"
    )]
    [InlineData(
        "{\"claude_prompt_context_version\":\"1\",\"claude_prompt_context_basis\":\"deduplicated_message_group\",\"context\":\"prompt_le_200000\"}"
    )]
    [InlineData(
        "{\"claude_prompt_context_version\":\"1\",\"claude_prompt_context_basis\":\"deduplicated_message_group\"}"
    )]
    public void InvalidMarkerOrContextIsUnknown(string raw)
    {
        var usage = ClaudeContextFixture.Event(raw);
        PricingEvidence.SourceDimensions(usage).Context.Should().BeNull();
        new AnthropicPriceCalculator().CalculateWithEvidence(usage, ClaudeContextFixture.Json()).Should().BeNull();
    }

    [Theory]
    [InlineData(ClaudeContextFixture.Lower, "standard", "standard", "global", 23.25, -2.25)]
    [InlineData(ClaudeContextFixture.Upper, "standard", "standard", "global", 61.5, -22.5)]
    [InlineData(ClaudeContextFixture.Lower, "batch", "standard", "us", 8.8125, -0.5625)]
    [InlineData(ClaudeContextFixture.Upper, "batch", "standard", "us", 28.90625, -7.03125)]
    [InlineData(ClaudeContextFixture.Lower, "standard", "fast", "global", 116.25, -11.25)]
    [InlineData(ClaudeContextFixture.Upper, "standard", "fast", "global", 138.25, -50.625)]
    public void ExplicitBandSelectsOwnOutputAndCacheRates(
        string context,
        string tier,
        string speed,
        string geo,
        double cost,
        double savings
    )
    {
        var usage = ClaudeContextFixture.Event(ClaudeContextFixture.Raw(context, tier, speed, geo));
        usage.OutputTokens = 1_000_000;
        usage.CacheReadTokens = 1_000_000;
        usage.CacheWriteTokens = 2_000_000;
        usage.CacheWrite1hTokens = 1_000_000;
        var result = new AnthropicPriceCalculator().CalculateWithEvidence(usage, ClaudeContextFixture.Json());
        result.Should().NotBeNull();
        result.Quote.Should().Be(new UsagePriceQuote((decimal)cost, (decimal)savings));
        result.RateKey.Should().Be(PricingEvidence.RateKey(ClaudeContextFixture.Entry(context)));
        result.Dimensions.Context.Should().Be(context);
        PricingEvidence.SourceDimensions(usage).Context.Should().Be(context);
    }

    [Fact]
    public void UntieredLegacyQuoteAndDigestUnchanged()
    {
        var usage = ClaudeContextFixture.Event("{}");
        usage.Model = "claude-sonnet-5";
        var catalog = ClaudeContextFixture.Catalog(
            ClaudeContextFixture.Entry(null) with
            {
                ModelPrefix = usage.Model,
                Aliases = [usage.Model],
            }
        );
        var result = new AnthropicPriceCalculator().CalculateWithEvidence(usage, PricingCatalogJson.Serialize(catalog));
        result!.Quote.Should().Be(new UsagePriceQuote(2m, 0m));
        result.Dimensions.Context.Should().BeNull();
        PricingEvidence.SourceDimensions(usage).Context.Should().BeNull();
        PricingEvidence
            .RevisionDigest(usage)
            .Should()
            .Be("b401ed43e559c9b9d0d098f9764b1973cc85ec7c4683119e140caef597a52b1e");
    }

    [Theory]
    [InlineData(Provider.OpenAI)]
    [InlineData(Provider.Google)]
    public void ClaudeMarkerDoesNotChangeOtherProviderSourceDimensions(Provider provider)
    {
        var usage = ClaudeContextFixture.Event(ClaudeContextFixture.Raw(ClaudeContextFixture.Lower));
        usage.Provider = provider;
        PricingEvidence.SourceDimensions(usage).Context.Should().BeNull();
    }

    [Fact]
    public void ProviderApiBucketCannotCopyLocalGroupedEvidence()
    {
        var usage = ClaudeContextFixture.Event(ClaudeContextFixture.Raw(ClaudeContextFixture.Lower));
        usage.SourceKind = SourceKind.ProviderApi;
        PricingEvidence.SourceDimensions(usage).Context.Should().BeNull();
        new AnthropicPriceCalculator().CalculateWithEvidence(usage, ClaudeContextFixture.Json()).Should().BeNull();
    }

    [Theory]
    [InlineData("claude-haiku-5-5", true)]
    [InlineData("claude-haiku-5-5-20261022", true)]
    [InlineData("haiku-qualified", true)]
    [InlineData("claude-haiku-5-5-20260230", false)]
    [InlineData("claude-haiku-5-50", false)]
    public void HistoricalClaimUsesOwnCatalogFlatEraWithoutFabricatingBandProvenance(string model, bool priced)
    {
        var flat = ClaudeContextFixture.Entry(null) with
        {
            EffectiveFrom = new(2026, 9, 1),
            Aliases = ["haiku-qualified"],
        };
        var catalog = ClaudeContextFixture.Catalog(
            flat,
            ClaudeContextFixture.Entry(ClaudeContextFixture.Lower),
            ClaudeContextFixture.Entry(ClaudeContextFixture.Upper)
        );
        var usage = ClaudeContextFixture.Event(ClaudeContextFixture.Raw(ClaudeContextFixture.Upper));
        usage.Model = model;
        usage.CostBasis = CostBasis.ListPriceEstimate;
        usage.OccurredAt = Instant.FromUtc(2026, 9, 20, 0, 0);
        var quote = new AnthropicPriceCalculator().CalculateWithEvidence(usage, PricingCatalogJson.Serialize(catalog));
        (quote is not null).Should().Be(priced);
        if (priced)
        {
            quote!.RateKey.Should().Be(PricingEvidence.RateKey(flat));
            quote.Dimensions.Context.Should().BeNull();
            quote.Quote.CostUsd.Should().Be(2m);
        }
    }

    [Theory]
    [InlineData("claude-haiku-5-5", true)]
    [InlineData("CLAUDE-HAIKU-5-5", true)]
    [InlineData("claude-haiku-5-5-20261022", true)]
    [InlineData("haiku-qualified", true)]
    [InlineData("claude-haiku-5-5-extra", false)]
    [InlineData("claude-haiku-5-50", false)]
    [InlineData("claude-haiku-5-5-20260230", false)]
    public void ProspectiveBandUsesQualifiedCatalogIdentity(string model, bool priced)
    {
        var catalog = ClaudeContextFixture.Catalog(
            ClaudeContextFixture.Entry(ClaudeContextFixture.Lower) with
            {
                Aliases = ["haiku-qualified"],
            },
            ClaudeContextFixture.Entry(ClaudeContextFixture.Upper) with
            {
                Aliases = ["haiku-qualified"],
            }
        );
        var usage = ClaudeContextFixture.Event(ClaudeContextFixture.Raw(ClaudeContextFixture.Upper));
        usage.Model = model;
        var result = new AnthropicPriceCalculator().CalculateWithEvidence(usage, PricingCatalogJson.Serialize(catalog));
        (result is not null).Should().Be(priced);
        if (priced)
        {
            result!.Dimensions.Context.Should().Be(ClaudeContextFixture.Upper);
            result.RateKey.Should().Be(PricingEvidence.RateKey(catalog.Entries[1]));
        }
    }

    [Theory]
    [InlineData("claude-haiku-5-5", true)]
    [InlineData("claude-haiku-5-5-20261022", true)]
    [InlineData("claude-haiku-5-5-extra", false)]
    [InlineData("claude-haiku-5-50", false)]
    [InlineData("claude-sonnet-5", false)]
    public void LegacyOnlyProspectiveGuardIsBoundedToQualifiedHaiku(string model, bool requiresBand)
    {
        var usage = ClaudeContextFixture.Event(ClaudeContextFixture.Raw(null));
        usage.Model = model;
        var entry = ClaudeContextFixture.Entry(null) with { ModelPrefix = model, Aliases = [model] };
        var result = new AnthropicPriceCalculator().CalculateWithEvidence(
            usage,
            PricingCatalogJson.Serialize(ClaudeContextFixture.Catalog(entry))
        );
        (result is null).Should().Be(requiresBand);
        if (!requiresBand)
        {
            result!.RateKey.Should().Be(PricingEvidence.RateKey(entry));
            result.Quote.CostUsd.Should().Be(2m);
        }
    }
}

internal static class ClaudeContextFixture
{
    internal const string Lower = "prompt_le_100000";
    internal const string Upper = "prompt_gt_100000";
    internal static readonly Instant Now = Instant.FromUtc(2026, 10, 22, 12, 0);
    internal static readonly LocalDate BandedFrom = new(2026, 10, 1);

    internal static UsageEvent Event(string raw) =>
        new()
        {
            Provider = Provider.Anthropic,
            Model = "claude-haiku-5-5",
            OccurredAt = Now,
            ObservedAt = Now,
            IngestedAt = Now,
            InputTokens = 1_000_000,
            CostBasis = CostBasis.Notional,
            SourceKind = SourceKind.LocalTelemetry,
            SourceId = "claude-local:test",
            RawPayload = raw,
        };

    internal static string Raw(
        string? context,
        string tier = "standard",
        string speed = "standard",
        string geo = "global"
    ) =>
        System.Text.Json.JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["claude_prompt_context_version"] = "1",
                ["claude_prompt_context_basis"] = "deduplicated_message_group",
                ["context"] = context,
                ["service_tier"] = tier,
                ["speed"] = speed,
                ["inference_geo"] = geo,
                ["cache_creation"] = new { ephemeral_5m_input_tokens = 1000000, ephemeral_1h_input_tokens = 1000000 },
            }
        );

    internal static AnthropicPriceEntry Entry(string? context) =>
        new(
            "claude-haiku-5-5",
            ["claude-haiku-5-5"],
            BandedFrom,
            true,
            context == Upper ? 4m : 2m,
            context == Upper ? 23m : 13m,
            context == Upper ? 1.5m : 0.75m,
            context == Upper ? 11m : 3.5m,
            context == Upper ? 22m : 4m,
            context == Upper ? 1m : 0.4m,
            context == Upper ? 13.5m : 5m,
            context == Upper ? 9m : 10m,
            context == Upper ? 51.625m : 65m,
            1.25m
        )
        {
            Context = context,
        };

    internal static AnthropicPriceCatalog Catalog(params AnthropicPriceEntry[] entries) =>
        new("USD", "https://example.test/pricing", Now, entries.Length == 0 ? [Entry(Lower), Entry(Upper)] : entries);

    internal static string Json() => PricingCatalogJson.Serialize(Catalog());
}
