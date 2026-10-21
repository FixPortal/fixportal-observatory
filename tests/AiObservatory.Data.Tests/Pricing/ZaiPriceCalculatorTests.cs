using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing;
using AiObservatory.Data.Pricing.Catalogs;
using AwesomeAssertions;
using NodaTime;

namespace AiObservatory.Data.Tests.Pricing;

public sealed class ZaiPriceCalculatorTests
{
    private static readonly LocalDate EffectiveFrom = new(2026, 10, 18);

    [Theory]
    [InlineData("glm-5.3", 6.06, 1.14)]
    [InlineData("glm-5.3-flash", 0.68, 0.12)]
    public void PricesEachModelAtItsOwnRates(string model, double expectedCost, double expectedSavings)
    {
        var quote = new ZaiPriceCalculator().Calculate(Event(model, cacheRead: 1_000_000), Catalog());
        quote.Should().Be(new UsagePriceQuote((decimal)expectedCost, (decimal)expectedSavings));
    }

    [Theory]
    [InlineData("glm-5.3-flashx")]
    [InlineData("glm-5")]
    [InlineData("GLM")]
    public void ReturnsNullForAModelNotInTheCatalog(string model)
    {
        new ZaiPriceCalculator().Calculate(Event(model), Catalog()).Should().BeNull();
    }

    [Fact]
    public void ReturnsNullWhenCachedTokensHaveNoPublishedRate()
    {
        var catalog = PricingCatalogJson.Serialize(
            new ZaiPriceCatalog(
                "USD",
                "https://docs.z.ai/guides/overview/pricing",
                EffectiveFrom.AtMidnight().InUtc().ToInstant(),
                [new ZaiPriceEntry("glm-5.3", EffectiveFrom, false, 1.4m, 4.4m, null)]
            )
        );
        new ZaiPriceCalculator().Calculate(Event("glm-5.3", cacheRead: 1_000), catalog).Should().BeNull();
    }

    [Fact]
    public void NotionalBasisPricesAtTheCatalogRetrievalDateEvenForEarlierUsage()
    {
        var earlier = EffectiveFrom.PlusDays(-30);
        var notional = Event("glm-5.3", occurredOn: earlier, basis: CostBasis.Notional);

        new ZaiPriceCalculator().Calculate(notional, Catalog())!.CostUsd.Should().Be(5.8m);
    }

    private static string Catalog() =>
        PricingCatalogJson.Serialize(
            new ZaiPriceCatalog(
                "USD",
                "https://docs.z.ai/guides/overview/pricing",
                EffectiveFrom.AtMidnight().InUtc().ToInstant(),
                [
                    new ZaiPriceEntry("glm-5.3", EffectiveFrom, false, 1.4m, 4.4m, 0.26m),
                    new ZaiPriceEntry("glm-5.3-flash", EffectiveFrom, false, 0.15m, 0.5m, 0.03m),
                ]
            )
        );

    private static UsageEvent Event(
        string model,
        long cacheRead = 0,
        LocalDate? occurredOn = null,
        CostBasis basis = CostBasis.ListPriceEstimate
    ) =>
        new()
        {
            Provider = Provider.Zai,
            Model = model,
            OccurredAt = (occurredOn ?? EffectiveFrom).AtMidnight().InUtc().ToInstant(),
            InputTokens = 1_000_000,
            OutputTokens = 1_000_000,
            CacheReadTokens = cacheRead,
            CostBasis = basis,
        };
}
