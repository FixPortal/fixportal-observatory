using AiObservatory.Audit.Checks;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using AiObservatory.Audit.Sources;
using AwesomeAssertions;

namespace AiObservatory.Audit.Tests.Sources;

public sealed class SourceAccountingCheckTests
{
    [Fact]
    public void PartialCapture_SubtotalsCannotProveTokenMismatch()
    {
        var record = SourceFixture.Claude();
        var input = SourceFixture.Input(record);
        input = input with
        {
            Captures = [input.Captures[0] with { SkippedFiles = 1, DiscoveredFiles = 2 }],
            Events = [SourceFixture.Event(record, new(200, 40, 120, 80, 20, null))],
        };
        var outcome = new SourceAccountingCheck().Evaluate(input);
        outcome.Verdict.Should().Be(CheckVerdict.Incomplete);
        outcome.Findings.Should().NotContain(f => f.RuleCode == "source_accounting_difference");
        new SourceAccountingCheck()
            .Evaluate(AuditCanonicalJson.Deserialize<AuditInput>(AuditCanonicalJson.Serialize(input)))
            .Should()
            .BeEquivalentTo(outcome);
    }

    [Fact]
    public void PartialSource_IndependentProvenSourceMismatchStillDominatesAndReplays()
    {
        var first = SourceFixture.Claude();
        var second = first with { SourceId = "claude-second", SessionId = "s2" };
        var partial = SourceFixture.Input(first);
        var proven = SourceFixture.Input(second);
        var input = partial with
        {
            Scope = partial.Scope with
            {
                Sources = [new(SourceFixture.Machine, first.SourceId), new(SourceFixture.Machine, second.SourceId)],
            },
            Records = [first, second],
            Captures = [partial.Captures[0] with { DiscoveredFiles = 2, SkippedFiles = 1 }, proven.Captures[0]],
            Events =
            [
                SourceFixture.Event(first, new(200, 40, 120, 80, 20, null)),
                SourceFixture.Event(second, new(99, 20, 60, 40, 10, null)),
            ],
        };
        var result = new AuditVerifier().Verify(input);
        result.Verdict.Should().Be(CheckVerdict.Mismatched);
        var difference = result
            .Findings.Should()
            .ContainSingle(f => f.RuleCode == "source_accounting_difference")
            .Subject;
        difference.Actual.Should().Be(new AuditValue(AuditValueKind.Integer, "99"));
        var replay = new AuditVerifier().Verify(
            AuditCanonicalJson.Deserialize<AuditInput>(AuditCanonicalJson.Serialize(input))
        );
        AuditCanonicalJson.Serialize(replay).Should().Equal(AuditCanonicalJson.Serialize(result));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DailySourceAttribution_IndependentOfServerSweepAlignment(bool unknown)
    {
        var first = SourceFixture.Codex();
        var later = first with
        {
            Sequence = 2,
            Timestamp = first.Timestamp.AddHours(2),
            Counters = new CodexCounters(180, 100, 30, 9, 210),
        };
        var input = SourceFixture.Input(first, later) with
        {
            SourceQualifications = [new(first.SourceId, unknown ? "revision_unverified" : "temporal_skew", unknown)],
        };
        new DailyAttributionCheck().Evaluate(input).Verdict.Should().Be(CheckVerdict.Mismatched);
    }

    [Theory]
    [InlineData(true, 100, CheckVerdict.Matched)]
    [InlineData(true, 99, CheckVerdict.Incomplete)]
    [InlineData(false, 100, CheckVerdict.Incomplete)]
    [InlineData(false, 99, CheckVerdict.Incomplete)]
    public void FrozenTemporalQualification_UnknownRevisionAllowsOnlyLimitedHistoricalMatch(
        bool unknown,
        long tokens,
        CheckVerdict verdict
    )
    {
        var record = SourceFixture.Claude();
        var input = SourceFixture.Input(record) with
        {
            Events = [SourceFixture.Event(record, new(tokens, 20, 60, 40, 10, null))],
            SourceQualifications = [new(record.SourceId, unknown ? "revision_unverified" : "temporal_skew", unknown)],
        };
        var outcome = new SourceAccountingCheck().Evaluate(input);
        outcome.Verdict.Should().Be(verdict);
        outcome.Findings.Should().Contain(f => f.DiagnosticCode == (unknown ? "revision_unverified" : "temporal_skew"));
        var bytes = AuditCanonicalJson.Serialize(input);
        new SourceAccountingCheck()
            .Evaluate(AuditCanonicalJson.Deserialize<AuditInput>(bytes))
            .Should()
            .BeEquivalentTo(outcome);
    }

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
        AuditCapabilities.Sources.Should().HaveCount(9);
        AuditCapabilities.Sources.Count(c => c.Supported).Should().Be(3);
        AuditCapabilities
            .Sources.Where(c => c.Tool == SourceTool.Claude)
            .Select(c => c.Version)
            .Should()
            .BeEquivalentTo("1", "2");
        AuditCapabilities
            .Sources.Where(c => c.Tool == SourceTool.Codex)
            .Should()
            .ContainSingle()
            .Which.Version.Should()
            .Be("1");
        AuditCapabilities.Sources.Where(c => !c.Supported).Should().OnlyContain(c => c.ReasonCode != null);
    }
}
