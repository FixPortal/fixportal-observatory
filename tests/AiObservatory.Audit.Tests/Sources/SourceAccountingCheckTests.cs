using AiObservatory.Audit.Checks;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using AiObservatory.Audit.Sources;
using AwesomeAssertions;

namespace AiObservatory.Audit.Tests.Sources;

public sealed class SourceAccountingCheckTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndependentSourceDimensions_ReconcileWithoutHistoricalCatalogAssociation(bool wrongCatalogDimensions)
    {
        var record = SourceFixture.Claude();
        var frozen = SourceFixture.Event(record, new(100, 20, 60, 40, 10, null));
        frozen = frozen with
        {
            SourceDimensions = record.Pricing,
            PricingApplication = wrongCatalogDimensions
                ? frozen.PricingApplication! with
                {
                    Dimensions = new(ServiceTier.Fast, true, "us", 1),
                }
                : null,
        };
        var input = SourceFixture.Input(record) with { Events = [frozen] };
        new SourceAccountingCheck().Evaluate(input).Verdict.Should().Be(CheckVerdict.Matched);
        if (!wrongCatalogDimensions)
        {
            input.Events[0].PricingApplication.Should().BeNull();
        }
        AuditComputationInput
            .ContentDigest(input)
            .Should()
            .NotBe(
                AuditComputationInput.ContentDigest(input with { Events = [frozen with { SourceDimensions = null }] })
            );
    }

    [Fact]
    public void SeparateSessionsAndGenerations_ConsolidateAtCompleteStoredComparisonKey()
    {
        var first = SourceFixture.Claude();
        var second = first with { Generation = "g2", SessionId = "s2" };
        var input = SourceFixture.Input(first, second) with
        {
            Events = [SourceFixture.Event(first, new(200, 40, 120, 80, 20, null))],
        };
        new SourceAccountingCheck().Evaluate(input).Verdict.Should().Be(CheckVerdict.Matched);
        var wrongScope = input.Events[0] with
        {
            Provenance = new(SourceKind.LocalTelemetry, UsageScope.Api, AccountingBasis.Notional),
        };
        new SourceAccountingCheck()
            .Evaluate(input with { Events = [wrongScope] })
            .Verdict.Should()
            .Be(CheckVerdict.Mismatched);
    }

    [Fact]
    public void TokenDifference_ReportsSignedExactFinding()
    {
        var record = SourceFixture.Claude();
        var input = SourceFixture.Input(record) with
        {
            Events = [SourceFixture.Event(record, new(99, 20, 60, 40, 10, null))],
        };
        var outcome = new SourceAccountingCheck().Evaluate(input);
        outcome.Verdict.Should().Be(CheckVerdict.Mismatched);
        var finding = outcome.Findings.Should().ContainSingle(f => f.Field == "inputTokens").Subject;
        finding.Expected.Should().Be(new AuditValue(AuditValueKind.Integer, "100"));
        finding.Actual.Should().Be(new AuditValue(AuditValueKind.Integer, "99"));
        finding.Delta.Should().Be(new AuditValue(AuditValueKind.Integer, "-1"));
    }

    [Fact]
    public void EmptySourcesWithoutAffirmativeCoverage_AreIncomplete()
    {
        new SourceAccountingCheck().Evaluate(SourceFixture.Input()).Verdict.Should().Be(CheckVerdict.Incomplete);
        new DailyAttributionCheck().Evaluate(SourceFixture.Input()).Verdict.Should().Be(CheckVerdict.Incomplete);
    }

    [Fact]
    public void DailyCheck_ExposesCompatibilityDateVersusChronologicalObservation()
    {
        var first = SourceFixture.Codex();
        var later = first with
        {
            Sequence = 2,
            Timestamp = first.Timestamp.AddHours(2),
            Counters = new CodexCounters(180, 100, 30, 9, 210),
        };
        var outcome = new DailyAttributionCheck().Evaluate(SourceFixture.Input(first, later));
        outcome.Verdict.Should().Be(CheckVerdict.Mismatched);
        outcome.Findings.Should().Contain(f => f.DiagnosticCode == "daily_attribution_difference");
    }

    [Fact]
    public void CapabilityMatrix_DeclaresAllProducersAndUnsupportedReasonCodes()
    {
        AuditCapabilities.Sources.Should().HaveCount(8);
        AuditCapabilities.Sources.Count(c => c.Supported).Should().Be(2);
        AuditCapabilities.Sources.Where(c => !c.Supported).Should().OnlyContain(c => c.ReasonCode != null);
    }
}
