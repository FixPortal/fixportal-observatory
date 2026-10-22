using AiObservatory.Audit.Checks;
using AiObservatory.Audit.Contracts;
using AwesomeAssertions;

namespace AiObservatory.Audit.Tests.Sources;

public sealed class CodexBoundaryComparisonTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FinalOutsideWindowDoesNotProveDailyGroupAbsence(bool closed)
    {
        var input = SourceFixture.Input(Straddling(closed));
        var result = new DailyAttributionCheck().Evaluate(input);
        result.Verdict.Should().Be(CheckVerdict.Incomplete);
        result.Findings.Should().NotContain(f => f.RuleCode == "daily_attribution_difference");
        result.Coverage.Gaps.Should().Contain(g => g.DiagnosticCode == "daily_attribution_boundary");
        var source = new SourceAccountingCheck().Evaluate(input);
        source.Verdict.Should().Be(CheckVerdict.Incomplete);
        source.Findings.Should().NotContain(f => f.RuleCode == "source_accounting_difference");
    }

    [Theory]
    [InlineData(19, CheckVerdict.Incomplete)]
    [InlineData(20, CheckVerdict.Matched)]
    [InlineData(21, CheckVerdict.Matched)]
    [InlineData(22, CheckVerdict.Incomplete)]
    public void ClosedSessionWhollyBeforeInsideOrAfterScopeRetainsItsMeaning(int day, CheckVerdict verdict)
    {
        var record = SourceFixture.Codex() with
        {
            Timestamp = At(day),
            Closure = new(ClosureState.ExplicitlyClosed, At(day), 1),
        };
        var result = new DailyAttributionCheck().Evaluate(SourceFixture.Input(record));
        result.Verdict.Should().Be(verdict);
        result.Findings.Should().NotContain(f => f.RuleCode == "daily_attribution_difference");
        result.Coverage.Gaps.Should().NotContain(g => g.DiagnosticCode == "daily_attribution_boundary");
    }

    [Fact]
    public void BoundaryQualificationPreservesSameSourceIndependentModelMismatchAndSourceAccounting()
    {
        var basis = SourceFixture.Codex(11) with
        {
            FileId = "independent",
            SessionId = "independent",
            Model = "gpt-independent",
            Kind = UsageRecordKind.Baseline,
            Timestamp = At(19),
            Counters = new CodexCounters(50, 0, 0, 0, 50),
            Closure = new(ClosureState.Unknown, null, null),
        };
        var final = basis with
        {
            Sequence = 12,
            Kind = UsageRecordKind.CumulativeUsage,
            Timestamp = At(20),
            Counters = new CodexCounters(100, 0, 0, 0, 100),
            Closure = new(ClosureState.ExplicitlyClosed, At(20), 12),
        };
        var input = SourceFixture.Input([.. Straddling(true), basis, final]) with
        {
            Events = [SourceFixture.Event(final, new(100, 0, 0, null, null, 0)) with { Provider = "OpenAI" }],
        };
        var daily = new DailyAttributionCheck().Evaluate(input);
        daily.Verdict.Should().Be(CheckVerdict.Mismatched);
        var difference = daily
            .Findings.Where(f => f.RuleCode == "daily_attribution_difference")
            .Should()
            .ContainSingle()
            .Which;
        difference.Field.Should().Be("inputTokens");
        difference.Expected!.Value.Should().Be("50");
        difference.Actual!.Value.Should().Be("100");
        daily.Coverage.ComparedGroups.Should().Be(1);
        new SourceAccountingCheck().Evaluate(input).Verdict.Should().Be(CheckVerdict.Matched);
    }

    [Fact]
    public void BoundaryGapDoesNotSuppressAnIndependentProvenGroupPresenceDifference()
    {
        // Zero counters isolate the existing group-presence contract from numeric differences.
        var first = SourceFixture.Codex(11) with
        {
            FileId = "zero",
            SessionId = "zero",
            Model = "gpt-independent",
            Timestamp = At(20),
            Counters = new CodexCounters(0, 0, 0, 0, 0) { LastTokenUsage = new(0, 0, 0, 0, 0) },
            Closure = new(ClosureState.ExplicitlyClosed, At(21), 12),
        };
        var last = first with { Sequence = 12, Timestamp = At(21) };
        var result = new DailyAttributionCheck().Evaluate(SourceFixture.Input([.. Straddling(true), first, last]));
        result.Verdict.Should().Be(CheckVerdict.Mismatched);
        result
            .Findings.Where(f => f.RuleCode == "daily_attribution_difference")
            .Should()
            .ContainSingle()
            .Which.Field.Should()
            .Be("groupPresence");
    }

    private static SourceUsageRecord[] Straddling(bool closed)
    {
        var basis = SourceFixture.Codex() with
        {
            Kind = UsageRecordKind.Baseline,
            Timestamp = At(19),
            Counters = new CodexCounters(0, 0, 0, 0, 0),
            Closure = new(ClosureState.Unknown, null, null),
        };
        var inside = basis with
        {
            Sequence = 2,
            Kind = UsageRecordKind.CumulativeUsage,
            Timestamp = At(20),
            Counters = new CodexCounters(100, 20, 10, 0, 110),
        };
        var after = inside with
        {
            Sequence = 3,
            Timestamp = At(22),
            Counters = new CodexCounters(200, 40, 20, 0, 220),
            Closure = closed ? new(ClosureState.ExplicitlyClosed, At(22), 3) : inside.Closure,
        };
        return [basis, inside, after];
    }

    private static DateTimeOffset At(int day) => new(2026, 10, day, 12, 0, 0, TimeSpan.Zero);
}
