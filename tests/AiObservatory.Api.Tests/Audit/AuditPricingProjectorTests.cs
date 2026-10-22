using System.Text.Json.Nodes;
using AiObservatory.Api.Audit;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing;
using AiObservatory.Data.Pricing.Catalogs;
using AwesomeAssertions;
using NodaTime;

namespace AiObservatory.Api.Tests.Audit;

public sealed class AuditPricingProjectorTests
{
    [Theory]
    [InlineData(Provider.OpenAI)]
    [InlineData(Provider.Google)]
    public void BatchEntries_BindSelectedKeyAndEffectiveDimensions(Provider provider)
    {
        var now = Instant.FromUtc(2026, 10, 22, 12, 0);
        var usage = new UsageEvent
        {
            Provider = provider,
            Model = "synthetic",
            OccurredAt = now,
            InputTokens = 1_000_000,
            CostBasis = CostBasis.ListPriceEstimate,
        };
        string catalog;
        IProviderPriceCalculator calculator;
        if (provider == Provider.OpenAI)
        {
            catalog = PricingCatalogJson.Serialize(
                new OpenAiPriceCatalog(
                    "USD",
                    "https://example.com/pricing",
                    now,
                    [
                        new(
                            "synthetic",
                            ["synthetic"],
                            new(2026, 10, 1),
                            true,
                            "batch",
                            "short",
                            "global",
                            1m,
                            0.25m,
                            5m,
                            1.5m
                        ),
                    ]
                )
            );
            usage.RawPayload = """{"processing":"batch","context":"short","region":"global"}""";
            calculator = new OpenAiPriceCalculator();
        }
        else
        {
            catalog = PricingCatalogJson.Serialize(
                new GeminiDeveloperPriceCatalog(
                    "USD",
                    "https://example.com/pricing",
                    now,
                    [new("synthetic", ["synthetic"], new(2026, 10, 1), true, "batch", "short", 1m, 0.25m, 5m)]
                )
            );
            usage.RawPayload = """{"service":"Gemini Developer API","tier":"batch","context":"short"}""";
            calculator = new GooglePriceCalculator();
        }
        var applied = calculator.CalculateWithEvidence(usage, catalog)!;
        applied.Dimensions.Batch.Should().BeTrue();
        var frozen = AuditPricingProjector.Catalog(
            new()
            {
                Provider = provider,
                SourceId = "synthetic",
                ContentHash = new string('a', 64),
                RetrievedAt = now,
                NormalizedCatalog = catalog,
            }
        );
        frozen
            .Rates.Should()
            .OnlyContain(r => r.CatalogEntryKey == applied.RateKey && r.Dimensions == applied.Dimensions);
    }

    [Theory]
    [InlineData("exact", 0L, 100L)]
    [InlineData("missing", null, null)]
    [InlineData("contradictory", 100L, 0L)]
    public void AnthropicNotionalNonzeroOneHourWrites_FreezeDerivableDurationChoice(
        string observation,
        long? expected5m,
        long? expected1h
    )
    {
        var now = Instant.FromUtc(2026, 10, 22, 12, 0);
        var catalog = PricingCatalogJson.Serialize(
            new AnthropicPriceCatalog(
                "USD",
                "https://example.com/pricing",
                now,
                [
                    new(
                        "synthetic",
                        ["synthetic"],
                        new(2026, 10, 1),
                        true,
                        2m,
                        10m,
                        0.2m,
                        2.5m,
                        4m,
                        null,
                        null,
                        null,
                        null,
                        null
                    ),
                ]
            )
        );
        var raw = observation switch
        {
            "exact" => """{"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":100}}""",
            "contradictory" => """{"cache_creation":{"ephemeral_5m_input_tokens":100,"ephemeral_1h_input_tokens":0}}""",
            _ => "{}",
        };
        var usage = new UsageEvent
        {
            Provider = Provider.Anthropic,
            Model = "synthetic",
            OccurredAt = now,
            ObservedAt = now,
            IngestedAt = now,
            CacheWriteTokens = 100,
            CacheWrite1hTokens = 100,
            CostBasis = CostBasis.Notional,
            RawPayload = raw,
        };
        var quote = new AnthropicPriceCalculator().Calculate(usage, catalog)!;
        usage.CostUsd = quote.CostUsd;
        var frozen = AuditSnapshotProjector.Event(usage, null);
        frozen.Tokens.CacheWrite1hTokens.Should().Be(100);
        frozen
            .CacheDurations.Should()
            .Be(new AiObservatory.Audit.Contracts.CacheDurationObservation(expected5m, expected1h));
        var exact = frozen.CacheDurations.FiveMinuteTokens == 0 && frozen.CacheDurations.OneHourTokens == 100;
        frozen.CostUsd.Should().Be(exact ? 0.0004m : 0.00025m);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReorderedCatalogEntry_BindsSelectedIdentityToFrozenRate(bool hasGenericWriteRate)
    {
        var now = Instant.FromUtc(2026, 10, 22, 12, 0);
        var entry = new OpenAiPriceEntry(
            "synthetic",
            ["synthetic"],
            new(2026, 10, 1),
            true,
            "standard",
            "short",
            "global",
            2m,
            0.5m,
            10m,
            hasGenericWriteRate ? 3m : null
        );
        var wire = JsonNode.Parse(
            PricingCatalogJson.Serialize(new OpenAiPriceCatalog("USD", "https://example.com/pricing", now, [entry]))
        )!;
        var properties = wire["entries"]![0]!
            .AsObject()
            .Reverse()
            .Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value?.DeepClone()));
        wire["entries"]![0] = new JsonObject(properties);
        var json = wire.ToJsonString();
        var usage = new UsageEvent
        {
            Provider = Provider.OpenAI,
            Model = "synthetic",
            OccurredAt = now,
            InputTokens = 1_000_000,
            CostBasis = CostBasis.ListPriceEstimate,
            RawPayload = """{"processing":"standard","context":"short","region":"global"}""",
        };
        var applied = new OpenAiPriceCalculator().CalculateWithEvidence(usage, json)!;
        var frozen = AuditPricingProjector.Catalog(
            new()
            {
                Provider = Provider.OpenAI,
                SourceId = "synthetic",
                ContentHash = new string('a', 64),
                RetrievedAt = now,
                NormalizedCatalog = json,
            }
        );
        frozen.Rates.Should().OnlyContain(r => r.CatalogEntryKey == applied.RateKey);
        frozen
            .Rates.Count(r => r.Meter == AiObservatory.Audit.Contracts.PriceMeter.CacheWrite5m)
            .Should()
            .Be(hasGenericWriteRate ? 1 : 0);
    }

    [Theory]
    [InlineData("standard", "standard", "global")]
    [InlineData("batch", "standard", "global")]
    [InlineData("standard", "fast", "global")]
    [InlineData("standard", "standard", "us")]
    [InlineData("batch", "standard", "us")]
    [InlineData("standard", "fast", "us")]
    public void AnthropicResolvedLanes_BindToExactFrozenEntryAndEffectiveDimensions(
        string tier,
        string speed,
        string geo
    )
    {
        var now = Instant.FromUtc(2026, 10, 22, 12, 0);
        var entry = new AnthropicPriceEntry(
            "synthetic",
            ["synthetic"],
            new(2026, 10, 1),
            true,
            2m,
            10m,
            0.2m,
            2.5m,
            4m,
            1m,
            5m,
            4m,
            20m,
            1.1m
        );
        var catalog = PricingCatalogJson.Serialize(
            new AnthropicPriceCatalog("USD", "https://example.com/pricing", now, [entry])
        );
        var usage = new UsageEvent
        {
            Provider = Provider.Anthropic,
            Model = "synthetic",
            OccurredAt = now,
            InputTokens = 1_000_000,
            OutputTokens = 1_000_000,
            CostBasis = CostBasis.ListPriceEstimate,
            RawPayload = $"{{\"service_tier\":\"{tier}\",\"speed\":\"{speed}\",\"inference_geo\":\"{geo}\"}}",
        };
        var applied = new AnthropicPriceCalculator().CalculateWithEvidence(usage, catalog)!;
        var frozen = AuditPricingProjector.Catalog(
            new()
            {
                Provider = Provider.Anthropic,
                SourceId = "synthetic",
                ContentHash = new string('a', 64),
                RetrievedAt = now,
                NormalizedCatalog = catalog,
            }
        );
        var rates = frozen.Rates.Where(r => r.Dimensions == applied.Dimensions).ToArray();
        rates.Should().NotBeEmpty();
        rates.Should().OnlyContain(r => r.CatalogEntryKey == applied.RateKey);
        rates
            .Sum(r =>
                r.Meter
                    is AiObservatory.Audit.Contracts.PriceMeter.Input
                        or AiObservatory.Audit.Contracts.PriceMeter.Output
                    ? r.UnitPrice * (r.RegionMultiplier ?? 1m)
                    : 0m
            )
            .Should()
            .Be(applied.Quote.CostUsd);
    }
}
