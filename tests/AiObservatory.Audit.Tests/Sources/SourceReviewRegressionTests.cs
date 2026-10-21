using AiObservatory.Audit.Checks;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using AiObservatory.Audit.Sources;
using AwesomeAssertions;

namespace AiObservatory.Audit.Tests.Sources;

public sealed class SourceReviewRegressionTests
{
    [Fact]
    public void ClaudeTtlOnlyCopy_OutranksSmallerExplicitCreationCopy()
    {
        var smaller = SourceFixture.Claude() with { Counters = new ClaudeCounters(100, 20, 20, 60, null, null) };
        var richer = SourceFixture.Claude(2) with { Counters = new ClaudeCounters(100, 20, null, 60, 30, 10) };
        var adapter = new ClaudeAuditAdapter();
        var result = adapter.Reconstruct([smaller, richer], SourceFixture.Scope());
        result
            .CompatibleGroups.Should()
            .ContainSingle()
            .Which.Tokens.Should()
            .Be(new UsageCounters(100, 20, 60, 40, 10, null));
        result.Findings.Should().BeEmpty();
        AuditCanonicalJson
            .Serialize(result)
            .Should()
            .Equal(AuditCanonicalJson.Serialize(adapter.Reconstruct([richer, smaller], SourceFixture.Scope())));
    }

    [Fact]
    public void ClaudeTtlRichness_RejectsOverflowEvenWithAnotherUsableCopy()
    {
        var explicitCopy = SourceFixture.Claude() with
        {
            Counters = new ClaudeCounters(0, 0, long.MaxValue, 0, null, null),
        };
        var overflowingCopy = SourceFixture.Claude(2) with
        {
            Counters = new ClaudeCounters(0, 0, null, 0, long.MaxValue, 1),
        };
        var action = () => new ClaudeAuditAdapter().Reconstruct([explicitCopy, overflowingCopy], SourceFixture.Scope());
        action.Should().Throw<AuditValidationException>().Which.Code.Should().Be("counter_overflow");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MatchingMachineOrGenerationCannotHideAnotherPreWindowOnlyHistory(bool separateMachine)
    {
        var first = SourceFixture.Claude();
        var otherMachine = separateMachine ? Guid.Parse("00000000-0000-0000-0000-000000000002") : first.MachineId;
        var beforeWindow = first with
        {
            MachineId = otherMachine,
            Generation = separateMachine ? "g1" : "g2",
            FileId = "other-file",
            Timestamp = new(2026, 10, 19, 23, 0, 0, TimeSpan.Zero),
        };
        var input = SourceFixture.Input(first);
        var otherScope = input.Scope with { Sources = [new(otherMachine, first.SourceId)] };
        var otherCapture = input.Captures[0] with
        {
            CaptureId = Guid.NewGuid(),
            RequestId = Guid.NewGuid(),
            MachineId = otherMachine,
            Generation = beforeWindow.Generation,
            Scope = otherScope,
        };
        input = input with
        {
            Scope = input.Scope with { Sources = input.Scope.Sources.Concat(otherScope.Sources).Distinct().ToArray() },
            Records = [first, beforeWindow],
            Captures = [input.Captures[0], otherCapture],
            Events = [SourceFixture.Event(first, new(100, 20, 60, 40, 10, null))],
        };

        var accounting = new SourceAccountingCheck().Evaluate(input);
        accounting.Verdict.Should().Be(CheckVerdict.Incomplete);
        accounting.Coverage.Gaps.Should().Contain(g => g.DiagnosticCode == "no_usage_records");
        new DailyAttributionCheck().Evaluate(input).Verdict.Should().Be(CheckVerdict.Incomplete);
    }

    [Fact]
    public void ManifestDeclaringDifferentSupportedProducer_CannotCoverClaudeRecords()
    {
        var record = SourceFixture.Claude();
        var input = SourceFixture.Input(record);
        input = input with
        {
            Captures = [input.Captures[0] with { SourceFormats = [new(record.SourceId, "codex-jsonl", "1")] }],
            Events = [SourceFixture.Event(record, new(100, 20, 60, 40, 10, null))],
        };
        var accounting = new SourceAccountingCheck().Evaluate(input);
        accounting.Verdict.Should().Be(CheckVerdict.Incomplete);
        accounting.Coverage.Gaps.Should().Contain(g => g.DiagnosticCode == "incomplete_capture");
        new DailyAttributionCheck().Evaluate(input).Verdict.Should().Be(CheckVerdict.Incomplete);
    }
}
