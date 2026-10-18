using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing;
using AiObservatory.Data.Pricing.Catalogs;
using AwesomeAssertions;
using NodaTime;

namespace AiObservatory.Data.Tests.Pricing;

public sealed class DeepSeekPriceCalculatorTests
{
    private static readonly LocalDate EffectiveFrom = new(2026, 10, 18);

    // The standard rate applies whatever the instant: 2026-10-19 is a Monday (07:00 is peak,
    // 04:00 and 10:00 are the window edges, 12:00 is off-peak) and 2026-10-24 a Saturday.
    [Theory]
    [InlineData("deepseek-flash", "2026-10-19T07:00:00Z", 1.506, 0.294)]
    [InlineData("deepseek-flash", "2026-10-19T04:00:00Z", 1.506, 0.294)]
    [InlineData("deepseek-flash", "2026-10-19T10:00:00Z", 1.506, 0.294)]
    [InlineData("deepseek-flash", "2026-10-19T12:00:00Z", 1.506, 0.294)]
    [InlineData("deepseek-flash", "2026-10-24T07:00:00Z", 1.506, 0.294)]
    [InlineData("deepseek-v4-pro", "2026-10-19T07:00:00Z", 5.324, 1.276)]
    [InlineData("deepseek-v4-pro", "2026-10-19T04:00:00Z", 5.324, 1.276)]
    [InlineData("deepseek-v4-pro", "2026-10-24T07:00:00Z", 5.324, 1.276)]
    public void PricesAtTheStandardRateRegardlessOfTheEventTimestamp(
        string model,
        string at,
        double expectedCost,
        double expectedSavings
    )
    {
        var quote = new DeepSeekPriceCalculator().Calculate(Event(model, at), Catalog());
        quote.Should().Be(new UsagePriceQuote((decimal)expectedCost, (decimal)expectedSavings));
    }

    [Theory]
    [InlineData("deepseek-v4-flash")]
    [InlineData("deepseek")]
    public void ReturnsNullForAModelNotInTheCatalog(string model)
    {
        new DeepSeekPriceCalculator().Calculate(Event(model, "2026-10-19T12:00:00Z"), Catalog()).Should().BeNull();
    }

    [Fact]
    public void NotionalBasisPricesAtTheCatalogRetrievalDateEvenForEarlierUsage()
    {
        var notional = Event("deepseek-flash", "2026-09-14T12:00:00Z", CostBasis.Notional);

        new DeepSeekPriceCalculator().Calculate(notional, Catalog())!.CostUsd.Should().Be(1.506m);
    }

    private static string Catalog() =>
        PricingCatalogJson.Serialize(
            new DeepSeekPriceCatalog(
                "USD",
                "https://api-docs.deepseek.com/quick_start/pricing",
                EffectiveFrom.AtMidnight().InUtc().ToInstant(),
                [
                    new DeepSeekPriceEntry("deepseek-flash", EffectiveFrom, false, 0.3m, 1.2m, 0.006m),
                    new DeepSeekPriceEntry("deepseek-v4-pro", EffectiveFrom, false, 1.32m, 3.96m, 0.044m),
                ]
            )
        );

    private static UsageEvent Event(string model, string at, CostBasis basis = CostBasis.ListPriceEstimate) =>
        new()
        {
            Provider = Provider.DeepSeek,
            Model = model,
            OccurredAt = NodaTime.Text.InstantPattern.ExtendedIso.Parse(at).Value,
            InputTokens = 1_000_000,
            OutputTokens = 1_000_000,
            CacheReadTokens = 1_000_000,
            CostBasis = basis,
        };
}
