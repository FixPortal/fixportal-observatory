using AiObservatory.Audit.Checks;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using AiObservatory.Audit.Tests.Sources;
using AwesomeAssertions;

namespace AiObservatory.Audit.Tests.Checks;

public sealed class VerdictTests
{
    [Theory]
    [InlineData(CheckVerdict.Matched, CheckVerdict.Incomplete, CheckVerdict.Incomplete)]
    [InlineData(CheckVerdict.Unsupported, CheckVerdict.Matched, CheckVerdict.Incomplete)]
    [InlineData(CheckVerdict.Mismatched, CheckVerdict.Incomplete, CheckVerdict.Mismatched)]
    [InlineData(CheckVerdict.Unsupported, CheckVerdict.Mismatched, CheckVerdict.Mismatched)]
    [InlineData(CheckVerdict.Excluded, CheckVerdict.Matched, CheckVerdict.Matched)]
    [InlineData(CheckVerdict.Excluded, CheckVerdict.Excluded, CheckVerdict.Incomplete)]
    public void Reduction_PreservesMismatchAndRequiresAnExecutedCheck(
        CheckVerdict a,
        CheckVerdict b,
        CheckVerdict expected
    )
    {
        AuditVerdictReducer
            .Reduce([
                new(CheckCode.EventAggregation, a, new(0, 0, []), []),
                new(CheckCode.PresentationTotals, b, new(0, 0, []), []),
            ])
            .Should()
            .Be(expected);
        AuditVerdictReducer.Reduce([]).Should().Be(CheckVerdict.Incomplete);
    }

    [Fact]
    public void Verifier_WiresSourceChecksAndExplicitUnsupportedChecks()
    {
        var record = SourceFixture.Claude();
        var input = SourceFixture.Input(record) with
        {
            Events = [SourceFixture.Event(record, new(100, 20, 60, 40, 10, null))],
        };
        input = input with { Scope = input.Scope with { RequestedChecks = Enum.GetValues<CheckCode>() } };
        var result = new AuditVerifier().Verify(input);
        result.Checks.Should().Contain(c => c.Code == CheckCode.SourceAccounting && c.Verdict == CheckVerdict.Matched);
        result.Checks.Should().Contain(c => c.Code == CheckCode.DailyAttribution && c.Verdict == CheckVerdict.Matched);
        result
            .Checks.Should()
            .Contain(c => c.Code == CheckCode.PricingArithmetic && c.Verdict == CheckVerdict.Unsupported);
        result
            .Checks.Should()
            .Contain(c => c.Code == CheckCode.BillingReconciliation && c.Verdict == CheckVerdict.Unsupported);
        result.Verdict.Should().Be(CheckVerdict.Mismatched);
    }

    [Fact]
    public void ComputationDigestAndResult_IgnoreOperationalScopeLinks_IncludingCaptureScopes()
    {
        var input = SourceFixture.Input(SourceFixture.Claude());
        var linked = input with
        {
            Scope = input.Scope with { PredecessorRunId = Guid.NewGuid(), EvaluationOfRunId = Guid.NewGuid() },
            Captures = input
                .Captures.Select(c =>
                    c with
                    {
                        Scope = c.Scope with { PredecessorRunId = Guid.NewGuid(), EvaluationOfRunId = Guid.NewGuid() },
                    }
                )
                .ToArray(),
        };
        AuditCanonicalJson.Serialize(linked).Should().NotEqual(AuditCanonicalJson.Serialize(input));
        AuditComputationInput.ContentDigest(linked).Should().Be(AuditComputationInput.ContentDigest(input));
        AuditCanonicalJson
            .Serialize(new AuditVerifier().Verify(linked))
            .Should()
            .Equal(AuditCanonicalJson.Serialize(new AuditVerifier().Verify(input)));
        AuditComputationInput
            .ContentDigest(input with { Options = input.Options with { IncludeProvisionalAttribution = false } })
            .Should()
            .NotBe(AuditComputationInput.ContentDigest(input));
        AuditComputationInput
            .ContentDigest(input with { Events = [CheckFixture.Event()] })
            .Should()
            .NotBe(AuditComputationInput.ContentDigest(input));
    }

    [Fact]
    public void MissingReferenceCapture_CannotProveSourceTokenMismatch()
    {
        var record = SourceFixture.Claude();
        var input = SourceFixture.Input(record) with
        {
            Captures = [],
            Events = [SourceFixture.Event(record, new(99, 20, 60, 40, 10, null))],
        };
        var result = new AuditVerifier().Verify(input);
        result.Verdict.Should().Be(CheckVerdict.Incomplete);
        result.Coverage.Gaps.Should().Contain(g => g.DiagnosticCode == "incomplete_capture");
        result.Findings.Should().NotContain(f => f.RuleCode == "source_accounting_difference");
    }

    [Fact]
    public void ExcludedChecks_AreVisibleAndNeverExecuted()
    {
        var input = CheckFixture.Input(CheckFixture.Event());
        input = input with
        {
            Scope = input.Scope with { ExcludedChecks = [CheckCode.PresentationTotals, CheckCode.PricingArithmetic] },
            Aggregates = [CheckFixture.Aggregate()],
        };
        var result = new AuditVerifier().Verify(input);
        result.Verdict.Should().Be(CheckVerdict.Matched);
        result.Checks.Should().HaveCount(3);
        result
            .Checks.Should()
            .Contain(c => c.Code == CheckCode.PricingArithmetic && c.Verdict == CheckVerdict.Excluded);
    }

    [Fact]
    public void MissingSourceOrPricingDimensions_DoesNotProveANumericalMismatch()
    {
        var record = SourceFixture.Claude();
        var row = SourceFixture.Event(record, new(100, 20, 60, 40, 10, null));
        new SourceAccountingCheck()
            .Evaluate(SourceFixture.Input(record) with { Events = [row with { PricingApplication = null }] })
            .Verdict.Should()
            .Be(CheckVerdict.Incomplete);
        new SourceAccountingCheck()
            .Evaluate(SourceFixture.Input() with { Events = [row] })
            .Verdict.Should()
            .Be(CheckVerdict.Incomplete);
    }

    [Fact]
    public void UnreportedSourceCounter_IsNotAProvenZeroDifference()
    {
        var record = SourceFixture.Claude();
        var input = SourceFixture.Input(record) with
        {
            Events = [SourceFixture.Event(record, new(100, 20, 60, 40, 10, 0))],
        };
        var result = new SourceAccountingCheck().Evaluate(input);
        result.Verdict.Should().Be(CheckVerdict.Incomplete);
        result.Coverage.Gaps.Should().Contain(g => g.DiagnosticCode == "unknown_token_comparison");
    }

    [Fact]
    public void MixedPricedAndUnplacedEvents_DoNotPromotePartialSumIntoMismatch()
    {
        var record = SourceFixture.Claude();
        var placed = SourceFixture.Event(record, new(99, 20, 60, 40, 10, null));
        var unplaced = SourceFixture.Event(record, new(1, 0, 0, 0, 0, null)) with { PricingApplication = null };
        var input = SourceFixture.Input(record) with { Events = [placed, unplaced] };

        var result = new SourceAccountingCheck().Evaluate(input);
        result.Verdict.Should().Be(CheckVerdict.Incomplete);
        result.Coverage.ComparedGroups.Should().Be(0);
        result.Coverage.Gaps.Should().Contain(g => g.DiagnosticCode == "missing_pricing_dimensions");
        result.Findings.Should().NotContain(f => f.RuleCode == "source_accounting_difference");

        var classified = unplaced with { PricingApplication = placed.PricingApplication };
        new SourceAccountingCheck()
            .Evaluate(input with { Events = [placed, classified] })
            .Verdict.Should()
            .Be(CheckVerdict.Matched);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClassifiedGroupMismatch_StillDominatesUnplacedEventsInAnotherDateOrSource(bool differentSource)
    {
        var first = SourceFixture.Claude();
        var other = SourceFixture.Claude(2) with
        {
            MessageId = "m2",
            SourceId = differentSource ? "other-local" : first.SourceId,
            Timestamp = first.Timestamp.AddHours(2),
        };
        var placed = SourceFixture.Event(first, new(99, 20, 60, 40, 10, null));
        var unplaced = SourceFixture.Event(first, new(1, 0, 0, 0, 0, null)) with { PricingApplication = null };
        var otherEvent = SourceFixture.Event(other, new(99, 20, 60, 40, 10, null));
        var input = SourceFixture.Input(first, other);
        input = input with
        {
            Scope = input.Scope with
            {
                Sources = [new(SourceFixture.Machine, first.SourceId), new(SourceFixture.Machine, other.SourceId)],
            },
            Captures = input
                .Captures.Select(c =>
                    c with
                    {
                        Scope = c.Scope with
                        {
                            Sources =
                            [
                                new(SourceFixture.Machine, first.SourceId),
                                new(SourceFixture.Machine, other.SourceId),
                            ],
                        },
                        SourceFormats =
                        [
                            new(first.SourceId, first.SourceFormat, first.SourceVersion),
                            new(other.SourceId, other.SourceFormat, other.SourceVersion),
                        ],
                    }
                )
                .ToArray(),
            Events = [placed, unplaced, otherEvent],
        };

        var result = new SourceAccountingCheck().Evaluate(input);
        result.Verdict.Should().Be(CheckVerdict.Mismatched);
        result.Coverage.ComparedGroups.Should().Be(1);
        result.Coverage.Gaps.Should().Contain(g => g.DiagnosticCode == "missing_pricing_dimensions");
        var difference = result
            .Findings.Should()
            .ContainSingle(f => f.RuleCode == "source_accounting_difference" && f.Field == "inputTokens")
            .Subject;
        difference.Expected.Should().Be(new AuditValue(AuditValueKind.Integer, "100"));
        difference.Actual.Should().Be(new AuditValue(AuditValueKind.Integer, "99"));
        difference.Delta.Should().Be(new AuditValue(AuditValueKind.Integer, "-1"));
    }
}
