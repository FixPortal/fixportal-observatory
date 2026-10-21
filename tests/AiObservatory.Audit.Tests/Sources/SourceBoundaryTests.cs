using AiObservatory.Audit.Checks;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using AiObservatory.Audit.Sources;
using AwesomeAssertions;

namespace AiObservatory.Audit.Tests.Sources;

public sealed class SourceBoundaryTests
{
    [Fact]
    public void SourceCostsInDifferentCurrencies_AreNeverAddedIntoAnUnlabelledAmount()
    {
        var first = SourceFixture.Claude() with { Cost = 1m, Currency = "USD" };
        var second = first with { Sequence = 2, MessageId = "m2", Cost = 2m, Currency = "GBP" };
        var result = new ClaudeAuditAdapter().Reconstruct([first, second], SourceFixture.Scope());
        result.CompatibleGroups.Should().ContainSingle().Which.Cost.Should().BeNull();
        result.Findings.Should().Contain(f => f.DiagnosticCode == "mixed_source_currencies");
    }

    [Fact]
    public void ClaudeThinkingFacts_KeepExactSourceFieldNamesAtWireBoundary()
    {
        var counters = AuditCanonicalJson.Deserialize<ClaudeCounters>(
            "{\"input_tokens\":1,\"output_tokens\":2,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0,\"ephemeral_5m_input_tokens\":0,\"ephemeral_1h_input_tokens\":0,\"thinking_tokens\":3,\"thinking_output_tokens\":4}"u8
        );
        counters.ThinkingTokens.Should().Be(3);
        counters.ThinkingOutputTokens.Should().Be(4);
    }

    [Fact]
    public void IdenticalStoredEventCopies_DoNotCountUsageTwice()
    {
        var record = SourceFixture.Claude();
        var observed = SourceFixture.Event(record, new(100, 20, 60, 40, 10, null));
        var input = SourceFixture.Input(record) with { Events = [observed, observed] };
        new SourceAccountingCheck().Evaluate(input).Verdict.Should().Be(CheckVerdict.Matched);
    }

    [Fact]
    public void ClaudeEqualTotals_PreferCopyWithAdditionalTtlEvidence()
    {
        var partial = SourceFixture.Claude() with { Counters = new ClaudeCounters(100, 20, 40, 60, null, null) };
        var rich = SourceFixture.Claude(2);
        new ClaudeAuditAdapter()
            .Reconstruct([partial, rich], SourceFixture.Scope())
            .CompatibleGroups.Should()
            .ContainSingle()
            .Which.Tokens.CacheWrite1hTokens.Should()
            .Be(10);
    }

    [Fact]
    public void ClaudeCreation_DerivesFromBothReportedSubsetsWithoutDoubleCounting()
    {
        var record = SourceFixture.Claude() with
        {
            Counters = new ClaudeCounters(100, 20, null, 60, 30, 10) { ThinkingTokens = 5 },
        };
        var result = new ClaudeAuditAdapter().Reconstruct([record], SourceFixture.Scope());
        result
            .CompatibleGroups.Should()
            .ContainSingle()
            .Which.Tokens.Should()
            .Be(new UsageCounters(100, 20, 60, 40, 10, 5));
        result.Findings.Should().BeEmpty();
    }

    [Fact]
    public void ClaudeConflictingSubsetsAndAlternateThinkingConvention_AreIncomplete()
    {
        var record = SourceFixture.Claude() with
        {
            Counters = new ClaudeCounters(100, 20, 40, 60, 30, 20) { ThinkingOutputTokens = 5 },
        };
        var findings = new ClaudeAuditAdapter().Reconstruct([record], SourceFixture.Scope()).Findings;
        findings.Should().Contain(f => f.DiagnosticCode == "inconsistent_cache_subsets");
        findings.Should().Contain(f => f.DiagnosticCode == "unsupported_thinking_output_convention");
    }

    [Fact]
    public void CodexCopiedObservation_DoesNotTreatLastUsageAsAnotherIncrementOrDisagreement()
    {
        var first = SourceFixture.Codex();
        var result = new CodexAuditAdapter().Reconstruct(
            [first, first with { FileId = "copy" }],
            SourceFixture.Scope("codex-local")
        );
        result.Findings.Should().BeEmpty();
        result
            .ChronologicalGroups.Should()
            .ContainSingle()
            .Which.Tokens.Should()
            .Be(new UsageCounters(40, 10, 60, null, null, 4));
    }

    [Fact]
    public void CodexMissingClosure_MarksBothAccountingViewsProvisional()
    {
        var record = SourceFixture.Codex() with { Closure = new(ClosureState.Unknown, null, null) };
        var result = new CodexAuditAdapter().Reconstruct([record], SourceFixture.Scope("codex-local"));
        result.Findings.Should().Contain(f => f.DiagnosticCode == "missing_closure");
        result.CompatibleGroups.Should().OnlyContain(g => g.Provisional);
        result.ChronologicalGroups.Should().NotBeEmpty().And.OnlyContain(g => g.Provisional);
    }

    [Fact]
    public void CodexUnknownExplicitProvider_DoesNotFallbackToRecognizedModel()
    {
        var record = SourceFixture.Codex() with { Provider = "unrecognized" };
        var result = new CodexAuditAdapter().Reconstruct([record], SourceFixture.Scope("codex-local"));
        result.CompatibleGroups.Should().BeEmpty();
        result.Findings.Should().Contain(f => f.DiagnosticCode == "unknown_provider");
    }

    [Fact]
    public void CodexLastUsageDisagreement_ReportsUncertaintyWithoutReplacingCumulativeDelta()
    {
        var first = SourceFixture.Codex();
        var later = first with
        {
            Sequence = 2,
            Counters = new CodexCounters(180, 100, 30, 9, 210) { LastTokenUsage = new(1, 0, 1, 0, 2) },
            Timestamp = first.Timestamp.AddHours(2),
        };
        var result = new CodexAuditAdapter().Reconstruct([first, later], SourceFixture.Scope("codex-local"));
        result.Findings.Should().Contain(f => f.DiagnosticCode == "last_usage_disagreement");
        result.ChronologicalGroups[1].Tokens.Should().Be(new UsageCounters(40, 20, 40, null, null, 5));
    }

    [Fact]
    public void ComparisonPreservesExactLargeIntegersAndClaudeRawDimensions()
    {
        var record = SourceFixture.Claude() with
        {
            Counters = new ClaudeCounters(9007199254740993, 20, 40, 60, 30, 10),
            Pricing = SourceFixture.Pricing with
            {
                SourceServiceTier = "standard",
                Speed = "fast",
                InferenceGeo = "us",
            },
        };
        var observed = SourceFixture.Event(record, new(9007199254740992, 20, 60, 40, 10, null));
        var input = SourceFixture.Input(record) with { Events = [observed] };
        var result = new SourceAccountingCheck().Evaluate(input);
        result
            .Findings.Should()
            .Contain(f => f.Field == "inputTokens" && f.Delta == new AuditValue(AuditValueKind.Integer, "-1"));
        var wrongDimensions = observed with
        {
            Tokens = new(9007199254740993, 20, 60, 40, 10, null),
            PricingApplication = observed.PricingApplication! with
            {
                Dimensions = record.Pricing with { Speed = "normal" },
            },
        };
        new SourceAccountingCheck()
            .Evaluate(input with { Events = [wrongDimensions] })
            .Verdict.Should()
            .Be(CheckVerdict.Mismatched);
    }

    [Fact]
    public void IncompleteScanCannotCertifyMatchingRetainedUsage()
    {
        var record = SourceFixture.Claude();
        var input = SourceFixture.Input(record) with
        {
            Events = [SourceFixture.Event(record, new(100, 20, 60, 40, 10, null))],
        };
        input = input with { Captures = [input.Captures[0] with { ChangedFiles = 1 }] };
        new SourceAccountingCheck().Evaluate(input).Verdict.Should().Be(CheckVerdict.Incomplete);
    }

    [Fact]
    public void EmptyScanAndPositiveCompletenessDiagnosticCannotCertifyZeroConsumption()
    {
        var input = SourceFixture.Input(SourceFixture.Claude());
        input = input with
        {
            Records = [],
            Captures =
            [
                input.Captures[0] with
                {
                    DiscoveredFiles = 0,
                    ReadFiles = 0,
                    RecordCount = 0,
                    Diagnostics = [new("source_history_complete", 1)],
                },
            ],
        };
        new SourceAccountingCheck().Evaluate(input).Verdict.Should().Be(CheckVerdict.Incomplete);
    }

    [Fact]
    public void OnlyPreWindowHistoryCannotCertifyEmptyRequestedWindow()
    {
        var record = SourceFixture.Claude() with { Timestamp = new(2026, 10, 19, 1, 0, 0, TimeSpan.Zero) };
        new SourceAccountingCheck().Evaluate(SourceFixture.Input(record)).Verdict.Should().Be(CheckVerdict.Incomplete);
    }

    [Fact]
    public void UnsupportedSource_ReturnsUnsupportedWithNamedReason()
    {
        var record = SourceFixture.Claude() with
        {
            Tool = SourceTool.Gemini,
            Kind = UsageRecordKind.SessionMetadata,
            Counters = null,
        };
        var outcome = new SourceAccountingCheck().Evaluate(SourceFixture.Input(record));
        outcome.Verdict.Should().Be(CheckVerdict.Unsupported);
        outcome.Findings.Should().Contain(f => f.DiagnosticCode == "unsupported_gemini_accounting");
    }

    [Fact]
    public void ExcludedCheck_DoesNotEvaluateMissingFacts()
    {
        var input = SourceFixture.Input();
        input = input with
        {
            Scope = input.Scope with
            {
                RequestedChecks = [CheckCode.DailyAttribution],
                ExcludedChecks = [CheckCode.SourceAccounting],
            },
        };
        new SourceAccountingCheck().Evaluate(input).Verdict.Should().Be(CheckVerdict.Excluded);
    }

    [Fact]
    public void ComparisonConsolidation_UsesCheckedArithmetic()
    {
        var first = SourceFixture.Claude() with { Counters = new ClaudeCounters(long.MaxValue, 0, 0, 0, 0, 0) };
        var second = first with { Generation = "g2" };
        var action = () => new SourceAccountingCheck().Evaluate(SourceFixture.Input(first, second));
        action.Should().Throw<AuditValidationException>().Which.Code.Should().Be("counter_overflow");
    }

    [Fact]
    public void ReorderedSourceAndStoredEvents_ProduceIdenticalCanonicalOutcome()
    {
        var first = SourceFixture.Claude();
        var second = first with { Sequence = 2, MessageId = "m2", Model = "claude-opus" };
        var input = SourceFixture.Input(first, second) with
        {
            Events =
            [
                SourceFixture.Event(first, new(100, 20, 60, 40, 10, null)),
                SourceFixture.Event(second, new(100, 20, 60, 40, 10, null)),
            ],
        };
        var reordered = input with
        {
            Records = input.Records.Reverse().ToArray(),
            Events = input.Events.Reverse().ToArray(),
        };
        AuditCanonicalJson
            .Serialize(new SourceAccountingCheck().Evaluate(input))
            .Should()
            .Equal(AuditCanonicalJson.Serialize(new SourceAccountingCheck().Evaluate(reordered)));
    }
}
