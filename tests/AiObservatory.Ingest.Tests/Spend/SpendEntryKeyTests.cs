using AiObservatory.Data.Spend;
using AwesomeAssertions;
using NodaTime;

namespace AiObservatory.Ingest.Tests.Spend;

/// <summary>
/// The occurrence index is load-bearing. Without it two genuine identical charges on the
/// same day collide and the second silently vanishes — a quiet under-count, which is the
/// failure class this project has already been burned by.
/// </summary>
public class SpendEntryKeyTests
{
    private static readonly LocalDate Date = new(2026, 7, 12);

    [Fact]
    public void SameInputsProduceTheSameKey()
    {
        var a = SpendEntryKey.Derive(Date, "anthropic", 80.00m, "GBP", "Top-up", 0);
        var b = SpendEntryKey.Derive(Date, "anthropic", 80.00m, "GBP", "Top-up", 0);

        a.Should().Be(b, "re-importing the same file must be a no-op");
    }

    [Fact]
    public void OccurrenceIndexDistinguishesIdenticalCharges()
    {
        var first = SpendEntryKey.Derive(Date, "anthropic", 5.00m, "GBP", "Top-up", 0);
        var second = SpendEntryKey.Derive(Date, "anthropic", 5.00m, "GBP", "Top-up", 1);

        second.Should().NotBe(first, "two genuine identical charges must both survive");
    }

    [Theory]
    [InlineData("anthropic", 80.00, "GBP", "Top-up", true)]
    [InlineData("coderabbit", 80.00, "GBP", "Top-up", false)]
    [InlineData("anthropic", 80.01, "GBP", "Top-up", false)]
    [InlineData("anthropic", 80.00, "USD", "Top-up", false)]
    [InlineData("anthropic", 80.00, "GBP", "Credits", false)]
    public void EveryInputParticipatesInTheKey(
        string vendor,
        double amount,
        string currency,
        string description,
        bool shouldMatch
    )
    {
        var baseline = SpendEntryKey.Derive(Date, "anthropic", 80.00m, "GBP", "Top-up", 0);
        var candidate = SpendEntryKey.Derive(Date, vendor, (decimal)amount, currency, description, 0);

        if (shouldMatch)
        {
            candidate.Should().Be(baseline);
        }
        else
        {
            candidate.Should().NotBe(baseline);
        }
    }

    [Fact]
    public void DateParticipatesInTheKey()
    {
        var a = SpendEntryKey.Derive(Date, "anthropic", 80.00m, "GBP", "Top-up", 0);
        var b = SpendEntryKey.Derive(Date.PlusDays(1), "anthropic", 80.00m, "GBP", "Top-up", 0);

        b.Should().NotBe(a);
    }

    [Fact]
    public void NullAndEmptyDescriptionAreTheSame()
    {
        var withNull = SpendEntryKey.Derive(Date, "anthropic", 80.00m, "GBP", null, 0);
        var withEmpty = SpendEntryKey.Derive(Date, "anthropic", 80.00m, "GBP", "", 0);

        withNull.Should().Be(withEmpty, "a blank CSV cell and an absent one are the same charge");
    }

    [Fact]
    public void KeyFitsTheColumn()
    {
        var key = SpendEntryKey.Derive(Date, new string('v', 500), 80.00m, "GBP", new string('d', 500), 0);

        key.Length.Should().BeLessThanOrEqualTo(200, "EntryKey is varchar(200)");
    }

    [Fact]
    public void FieldsContainingPipesDoNotCollide()
    {
        var withPipeInCurrency = SpendEntryKey.Derive(Date, "anthropic", 80.00m, "USD|a", "b", 0);
        var withPipeInDescription = SpendEntryKey.Derive(Date, "anthropic", 80.00m, "USD", "a|b", 0);

        withPipeInCurrency.Should().NotBe(withPipeInDescription, "length-prefixing prevents pipe collisions");
    }
}
