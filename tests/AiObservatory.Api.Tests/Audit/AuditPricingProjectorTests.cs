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
    [Fact]
    public void ReorderedCatalogEntry_BindsSelectedIdentityToFrozenRate()
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
            3m
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
