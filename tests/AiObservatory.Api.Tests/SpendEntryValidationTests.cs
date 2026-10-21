using AiObservatory.Api.Endpoints;
using AiObservatory.Data.Entities;
using AwesomeAssertions;
using NodaTime;

namespace AiObservatory.Api.Tests;

/// <summary>
/// The gate every charge passes before it reaches the ledger. A rejection here is a per-row
/// verdict, not a failed batch — the POST takes an array precisely so one bad row does not
/// discard a whole CSV import or portal cycle.
/// </summary>
public class SpendEntryValidationTests
{
    private static readonly Guid KnownVendorId = Guid.NewGuid();
    private static readonly Guid KnownCategoryId = Guid.NewGuid();

    private static string? Validate(SpendEntryRequest req, out SpendSource source, out string currency) =>
        SpendEntriesEndpoints.Validate(req, [KnownVendorId], [KnownCategoryId], out source, out currency);

    private static SpendEntryRequest Request(
        decimal amount = 10m,
        string? currency = "GBP",
        string? description = null,
        string source = "Manual",
        string? entryKey = null,
        Guid? vendorId = null,
        Guid? categoryId = null
    ) =>
        new(
            new LocalDate(2026, 7, 1),
            vendorId ?? KnownVendorId,
            categoryId ?? KnownCategoryId,
            amount,
            currency,
            description,
            source,
            entryKey
        );

    [Fact]
    public void AcceptsASoundRequest()
    {
        var rejection = Validate(Request(), out var source, out var currency);

        rejection.Should().BeNull();
        source.Should().Be(SpendSource.Manual);
        currency.Should().Be("GBP");
    }

    [Fact]
    public void RejectsAnUnknownVendor()
    {
        var unknown = Guid.NewGuid();

        var rejection = Validate(Request(vendorId: unknown), out _, out _);

        rejection.Should().Be($"Unknown VendorId: {unknown}");
    }

    [Fact]
    public void RejectsAnUnknownCategory()
    {
        var unknown = Guid.NewGuid();

        var rejection = Validate(Request(categoryId: unknown), out _, out _);

        rejection.Should().Be($"Unknown CategoryId: {unknown}");
    }

    /// <summary>
    /// Only zero is barred. Negative is a refund or credit — see SpendEntry.Amount for why
    /// that is signed rather than flagged.
    /// </summary>
    [Fact]
    public void RejectsAZeroAmount()
    {
        Validate(Request(amount: 0m), out _, out _).Should().Be("Amount must not be zero");
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(-492.80)]
    [InlineData(0.01)]
    public void AcceptsAnyNonZeroAmount(decimal amount)
    {
        Validate(Request(amount: amount), out _, out _)
            .Should()
            .BeNull("a credit is a real ledger row, not a validation failure");
    }

    [Fact]
    public void DefaultsAnAbsentCurrencyToGbp()
    {
        Validate(Request(currency: null), out _, out var currency).Should().BeNull();

        currency.Should().Be("GBP");
    }

    [Theory]
    [InlineData("usd", "USD")]
    [InlineData(" eur ", "EUR")]
    [InlineData("Gbp", "GBP")]
    public void NormalisesTheCurrencyToUpperCase(string input, string expected)
    {
        Validate(Request(currency: input), out _, out var currency).Should().BeNull();

        currency
            .Should()
            .Be(expected, "the currency reaches FxRateProvider and the cache key, both of which are case-sensitive");
    }

    [Theory]
    [InlineData("US")]
    [InlineData("USDD")]
    [InlineData("US1")]
    [InlineData("US$")]
    [InlineData("")]
    [InlineData("   ")]
    public void RejectsACurrencyThatIsNotThreeAsciiLetters(string currency)
    {
        Validate(Request(currency: currency), out _, out _)
            .Should()
            .Be($"Currency must be a 3-letter ISO 4217 code, got: {currency}");
    }

    [Fact]
    public void AcceptsADescriptionAtTheLengthLimit()
    {
        Validate(Request(description: new string('x', 200)), out _, out _).Should().BeNull();
    }

    [Fact]
    public void RejectsADescriptionOverTheLengthLimit()
    {
        Validate(Request(description: new string('x', 201)), out _, out _)
            .Should()
            .Be("Description must be 200 characters or fewer");
    }

    [Fact]
    public void AcceptsAnEntryKeyAtTheLengthLimit()
    {
        Validate(Request(entryKey: new string('x', 200)), out _, out _).Should().BeNull();
    }

    [Fact]
    public void RejectsAnEntryKeyOverTheLengthLimit()
    {
        Validate(Request(entryKey: new string('x', 201)), out _, out _)
            .Should()
            .Be("EntryKey must be 200 characters or fewer");
    }

    [Theory]
    [InlineData("Manual", SpendSource.Manual)]
    [InlineData("manual", SpendSource.Manual)]
    [InlineData("PORTAL", SpendSource.Portal)]
    [InlineData("Api", SpendSource.Api)]
    [InlineData("Csv", SpendSource.Csv)]
    public void ParsesEveryDefinedSourceCaseInsensitively(string input, SpendSource expected)
    {
        Validate(Request(source: input), out var source, out _).Should().BeNull();

        source.Should().Be(expected);
    }

    /// <summary>
    /// <c>Enum.TryParse</c> alone accepts any numeric string as an enum value, so the
    /// <c>Enum.IsDefined</c> half is what stops an out-of-range source reaching the ledger —
    /// which would then render as a source nothing on the dashboard knows how to label.
    /// </summary>
    /// <summary>
    /// CK_SpendEntry_AmountGbp_SameSign rejects a zero AmountGbp, so a charge small enough to
    /// round to zero at the stored 4-decimal scale must be refused up front with an actionable
    /// message — the same one the billing writer produces — rather than the opaque
    /// "Could not save this entry" the DbUpdateException catch would return.
    /// </summary>
    [Theory]
    [InlineData(0.0000)]
    public void RejectsAnEntryWhoseConvertedAmountRoundsToZeroGbp(double amountGbp)
    {
        SpendEntriesEndpoints
            .RejectIfRoundsToZeroGbp((decimal)amountGbp)
            .Should()
            .Be("Amount converts to zero GBP at the stored 4-decimal scale — the entry is too small to record");
    }

    [Theory]
    [InlineData(0.0001)]
    [InlineData(-0.0001)]
    [InlineData(492.80)]
    public void AcceptsAnEntryWhoseConvertedAmountSurvivesRounding(double amountGbp)
    {
        SpendEntriesEndpoints.RejectIfRoundsToZeroGbp((decimal)amountGbp).Should().BeNull();
    }

    [Theory]
    [InlineData("99")]
    [InlineData("-1")]
    [InlineData("Statement")]
    [InlineData("")]
    public void RejectsASourceThatIsNotADefinedValue(string input)
    {
        Validate(Request(source: input), out _, out _).Should().Be($"Unknown source: {input}");
    }

    // AR-6: the provenance columns must follow the parsed Source, not hardcode the manual values.
    [Theory]
    [InlineData(SpendSource.Manual, UsageSourceIds.ManualLedger, SourceKind.Manual)]
    [InlineData(SpendSource.Csv, "csv-import", SourceKind.Manual)]
    [InlineData(SpendSource.Portal, "portal-feed", SourceKind.Manual)]
    [InlineData(SpendSource.Api, "vendor-api-entry", SourceKind.ProviderApi)]
    public void ProvenanceFollowsTheParsedSource(SpendSource source, string sourceId, SourceKind sourceKind)
    {
        SpendEntriesEndpoints.ProvenanceFor(source).Should().Be((sourceId, sourceKind));
    }
}
