using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using AiObservatory.Audit.Sources;
using AwesomeAssertions;

namespace AiObservatory.Audit.Tests.Sources;

public sealed class ClaudeAuditAdapterTests
{
    [Fact]
    public void RepeatedParentAndAgentCopies_CountRichestMessageOnceWithoutAddingTtlSubsets()
    {
        var rich = SourceFixture.Claude();
        var partial = rich with
        {
            FileId = "f2",
            SessionId = "child",
            ParentId = "s1",
            AgentId = "a1",
            Counters = new ClaudeCounters(100, 10, null, null, null, null),
        };
        var result = new ClaudeAuditAdapter().Reconstruct([partial, rich], SourceFixture.Scope());
        result
            .CompatibleGroups.Should()
            .ContainSingle()
            .Which.Tokens.Should()
            .Be(new UsageCounters(100, 20, 60, 40, 10, null));
        result.Coverage.Gaps.Should().BeEmpty();
    }

    [Fact]
    public void RichnessTie_UsesSequenceAndOpaqueIdentityRegardlessOfEnumerationOrder()
    {
        var first = SourceFixture.Claude();
        var tie = first with { FileId = "f2", Sequence = 2, Model = "claude-opus" };
        var adapter = new ClaudeAuditAdapter();
        var result = adapter.Reconstruct([tie, first], SourceFixture.Scope());
        result.CompatibleGroups.Should().ContainSingle().Which.Key.Model.Should().Be("claude-sonnet");
        AuditCanonicalJson
            .Serialize(result)
            .Should()
            .Equal(AuditCanonicalJson.Serialize(adapter.Reconstruct([first, tie], SourceFixture.Scope())));
    }

    [Fact]
    public void RotatedIdentityGeneration_DoesNotDedupeDistinctUsage()
    {
        var first = SourceFixture.Claude();
        var result = new ClaudeAuditAdapter().Reconstruct(
            [first, first with { Generation = "g2" }],
            SourceFixture.Scope()
        );
        result.CompatibleGroups.Should().HaveCount(2);
        result.CompatibleGroups.Sum(g => g.Tokens.InputTokens).Should().Be(200);
    }

    [Theory]
    [InlineData("closure")]
    [InlineData("model")]
    [InlineData("counter")]
    [InlineData("message")]
    [InlineData("format")]
    public void MissingEvidence_IsExplicitlyIncomplete(string missing)
    {
        var record = SourceFixture.Claude();
        record = missing switch
        {
            "closure" => record with { Closure = new(ClosureState.Unknown, null, null) },
            "model" => record with { Model = null },
            "counter" => record with { Counters = new ClaudeCounters(null, 20, 40, 60, 30, 10) },
            "message" => record with { MessageId = null },
            _ => record with { SourceVersion = "future" },
        };
        var result = new ClaudeAuditAdapter().Reconstruct([record], SourceFixture.Scope());
        result.Coverage.Gaps.Should().NotBeEmpty();
        result.Findings.Should().NotBeEmpty();
    }

    [Fact]
    public void SummationOverflow_IsSanitizedInsteadOfWrapping()
    {
        var first = SourceFixture.Claude() with { Counters = new ClaudeCounters(long.MaxValue, 0, 0, 0, 0, 0) };
        var second = first with { Sequence = 2, MessageId = "m2" };
        var action = () => new ClaudeAuditAdapter().Reconstruct([first, second], SourceFixture.Scope());
        action.Should().Throw<AuditValidationException>().Which.Code.Should().Be("counter_overflow");
    }
}
