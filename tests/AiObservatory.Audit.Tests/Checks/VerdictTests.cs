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
    public void ProvenSourceTokenMismatch_OverridesUnrelatedMissingCaptureCoverage()
    {
        var record = SourceFixture.Claude();
        var input = SourceFixture.Input(record) with
        {
            Captures = [],
            Events = [SourceFixture.Event(record, new(99, 20, 60, 40, 10, null))],
        };
        var result = new AuditVerifier().Verify(input);
        result.Verdict.Should().Be(CheckVerdict.Mismatched);
        result.Coverage.Gaps.Should().Contain(g => g.DiagnosticCode == "incomplete_capture");
        result
            .Findings.Should()
            .Contain(f => f.Field == "inputTokens" && f.Delta == new AuditValue(AuditValueKind.Integer, "-1"));
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
}
