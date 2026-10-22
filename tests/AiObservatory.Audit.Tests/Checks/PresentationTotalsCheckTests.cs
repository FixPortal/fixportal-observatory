using System.Globalization;
using System.Text.Json;
using AiObservatory.Audit.Checks;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using AwesomeAssertions;

namespace AiObservatory.Audit.Tests.Checks;

public sealed class PresentationTotalsCheckTests
{
    [Fact]
    public void SharedSummaryFixture_UsesNativeCurrenciesRefundsUnknownCountsAndRoundingLiterals()
    {
        using var fixture = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "dashboard-summary.v1.json"))
        );
        var root = fixture.RootElement;
        var events = root.GetProperty("events")
            .EnumerateArray()
            .Select(
                (e, i) =>
                    CheckFixture.Event(i + 1) with
                    {
                        Provenance = CheckFixture.Event().Provenance with
                        {
                            CostBasis = Enum.Parse<AccountingBasis>(e.GetProperty("basis").GetString()!, true),
                        },
                        CostUsd = Money(e, "costUsd"),
                        CacheSavingsUsd = Money(e, "savingsUsd"),
                    }
            )
            .ToArray();
        var spend = root.GetProperty("spend")
            .EnumerateArray()
            .Select(
                (e, i) =>
                    CheckFixture.Spend(
                        i + 100,
                        e.GetProperty("currency").GetString()!,
                        Money(e, "amount")!.Value,
                        Money(e, "amountGbp")!.Value
                    )
            )
            .ToArray();
        var expected = AuditCanonicalJson.Deserialize<PresentationSummaryValue[]>(
            System.Text.Encoding.UTF8.GetBytes(root.GetProperty("expected").GetRawText())
        );
        var input = CheckFixture.Input(events) with { Spend = spend };
        var check = new PresentationTotalsCheck();
        PresentationTotalsCheck.Reference(input).Should().BeEquivalentTo(expected);
        var result = check.Evaluate(WithProbe(input, expected));
        result.Verdict.Should().Be(CheckVerdict.Incomplete);
        result.Findings.Should().BeEmpty();
        foreach (var qualifier in root.GetProperty("missingQualifiers").EnumerateArray())
        {
            result.Coverage.Gaps.Should().Contain(g => g.DiagnosticCode == qualifier.GetString());
        }
    }

    [Fact]
    public void MissingProbe_DoesNotCertifyItsOwnReference()
    {
        var result = new PresentationTotalsCheck().Evaluate(CheckFixture.Input(CheckFixture.Event()));
        result.Verdict.Should().Be(CheckVerdict.Incomplete);
        result.Coverage.ComparedGroups.Should().Be(0);
        result.Coverage.Gaps.Should().Contain(g => g.DiagnosticCode == "missing_presentation_probe");
    }

    [Fact]
    public void CapturedProductionValue_IsComparedIndependentlyAndMismatchOverridesUnknowns()
    {
        var input = CheckFixture.Input(CheckFixture.Event() with { CostUsd = null });
        var values = CapturedValues(null, null, 1);
        var i = Array.FindIndex(values, v => v.Field == "unknownCostObservations");
        values[i] = values[i] with { UnroundedValue = 0, FormattedValue = "0" };
        var result = new PresentationTotalsCheck().Evaluate(WithProbe(input, values));
        result.Verdict.Should().Be(CheckVerdict.Mismatched);
        result
            .Findings.Should()
            .Contain(f =>
                f.Field == "unknownCostObservations"
                && f.Expected == new AuditValue(AuditValueKind.Integer, "1")
                && f.Actual == new AuditValue(AuditValueKind.Integer, "0")
                && f.Delta == new AuditValue(AuditValueKind.Integer, "-1")
            );
    }

    [Fact]
    public void ExactCapturedReference_Matches_AndStaleOrMissingBucketsAreIncomplete()
    {
        var input = CheckFixture.Input(CheckFixture.Event());
        var check = new PresentationTotalsCheck();
        var captured = WithProbe(input, CapturedValues());
        check.Evaluate(captured).Verdict.Should().Be(CheckVerdict.Matched);
        check
            .Evaluate(
                captured with
                {
                    PresentationProbe = captured.PresentationProbe! with { SummaryInputDigest = new string('b', 64) },
                }
            )
            .Coverage.Gaps.Should()
            .Contain(g => g.DiagnosticCode == "stale_presentation_probe");
        check.Evaluate(WithProbe(input, [])).Verdict.Should().Be(CheckVerdict.Incomplete);
    }

    [Fact]
    public void FormattingIsComparedEvenWhenUnroundedAmountMatches()
    {
        var input = CheckFixture.Input(CheckFixture.Event() with { CostUsd = 1.005m });
        var values = CapturedValues(1.005m, "1.00");
        new PresentationTotalsCheck()
            .Evaluate(WithProbe(input, values))
            .Findings.Should()
            .Contain(f => f.RuleCode == "presentation_format_difference" && f.Field == "notionalUsd");
    }

    [Fact]
    public void MissingAndAllUnknownBuckets_RemainNull_WhileKnownZeroIsReported()
    {
        var row = CheckFixture.Event() with { CostUsd = null, CacheSavingsUsd = null };
        var reference = PresentationTotalsCheck.Reference(CheckFixture.Input(row));
        reference
            .Should()
            .Contain(v => v.Field == "notionalUsd" && v.UnroundedValue == null && v.FormattedValue == null);
        reference.Should().Contain(v => v.Field == "billedGbp" && v.UnroundedValue == null);
        reference.Should().Contain(v => v.Field == "cacheSavingsUsd" && v.UnroundedValue == null);
        PresentationTotalsCheck
            .Reference(CheckFixture.Input(row with { CostUsd = 0 }))
            .Should()
            .Contain(v => v.Field == "notionalUsd" && v.UnroundedValue == 0 && v.FormattedValue == "0.00");
    }

    [Fact]
    public void PresentationComparesDisplayedPrecision_WithoutBinarySummationDustMismatch()
    {
        var input = CheckFixture.Input(CheckFixture.Event() with { CostUsd = 0.3m });
        var values = CapturedValues(0.30000000000000004m, "0.30");
        new PresentationTotalsCheck().Evaluate(WithProbe(input, values)).Verdict.Should().Be(CheckVerdict.Matched);
    }

    [Fact]
    public void CountsCompareExactly_EvenWhenAFractionWouldRoundToTheExpectedInteger()
    {
        var input = CheckFixture.Input(CheckFixture.Event() with { CostUsd = null });
        var values = CapturedValues(null, null, 1)
            .Select(v => v.Field == "unknownCostObservations" ? v with { UnroundedValue = 0.6m } : v)
            .ToArray();
        var result = new PresentationTotalsCheck().Evaluate(WithProbe(input, values));
        result.Verdict.Should().Be(CheckVerdict.Mismatched);
        result
            .Findings.Should()
            .Contain(f =>
                f.Field == "unknownCostObservations" && f.Actual == new AuditValue(AuditValueKind.Decimal, "0.6")
            );
    }

    [Fact]
    public void NoObservedCacheTraffic_DoesNotInventSavingsOrUnknownSavingsCoverage()
    {
        var row = CheckFixture.Event() with { Tokens = new(10, 20, null, null, null, null), CacheSavingsUsd = null };
        var reference = PresentationTotalsCheck.Reference(CheckFixture.Input(row));
        reference.Should().Contain(v => v.Field == "cacheSavingsUsd" && v.UnroundedValue == null);
        reference.Should().Contain(v => v.Field == "unknownCacheSavingsObservations" && v.UnroundedValue == 0);
    }

    [Fact]
    public void MalformedCapturedFormat_ProducesFindingWithoutLeakingAParserException()
    {
        var input = CheckFixture.Input(CheckFixture.Event());
        var values = CapturedValues(1.25m, "1..25");
        var result = new PresentationTotalsCheck().Evaluate(WithProbe(input, values));
        result.Verdict.Should().Be(CheckVerdict.Mismatched);
        result.Findings.Should().Contain(f => f.RuleCode == "presentation_format_difference" && f.Actual == null);
    }

    // Literal captured actuals, independent of Reference: one known notional posting, no ledger rows.
    private static PresentationSummaryValue[] CapturedValues(
        decimal? notional = 1.25m,
        string? formatted = "1.25",
        long unknown = 0
    ) =>
        [
            new("billedGbp", "all", null, null, "GBP"),
            new("listPriceEstimateUsd", "all", null, null, "USD"),
            new("providerEstimateUsd", "all", null, null, "USD"),
            new("notionalUsd", "all", notional, formatted, "USD"),
            new("unclassifiedUsd", "all", null, null, "USD"),
            new("cacheSavingsUsd", "all", -0.25m, "-0.25", "USD"),
            new("unknownCostObservations", "all", unknown, unknown.ToString(CultureInfo.InvariantCulture), "XXX"),
            new("unknownListPriceObservations", "all", 0, "0", "XXX"),
            new("unknownProviderEstimateObservations", "all", 0, "0", "XXX"),
            new("unknownNotionalObservations", "all", unknown, unknown.ToString(CultureInfo.InvariantCulture), "XXX"),
            new("unknownCacheSavingsObservations", "all", 0, "0", "XXX"),
        ];

    private static AuditInput WithProbe(AuditInput input, IReadOnlyList<PresentationSummaryValue> values) =>
        input with
        {
            PresentationProbe = new(
                CheckFixture.Digest,
                "synthetic-build",
                "synthetic-source",
                PresentationTotalsCheck.SummaryInputDigest(input),
                values
            ),
        };

    private static decimal? Money(JsonElement e, string field) =>
        e.GetProperty(field).ValueKind == JsonValueKind.Null
            ? null
            : decimal.Parse(e.GetProperty(field).GetString()!, CultureInfo.InvariantCulture);
}
