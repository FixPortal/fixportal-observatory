using System.Text.Json.Nodes;
using AiObservatory.Audit.Checks;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using AiObservatory.Audit.Sources;
using AwesomeAssertions;

namespace AiObservatory.Audit.Tests.Sources;

public sealed class ClaudeAuditAdapterTests
{
    [Fact]
    public void VersionOne_ReconstructionBytesRemainPinned()
    {
        var rich = SourceFixture.Claude() with { Model = "claude-haiku-5-5" };
        var partial = rich with { FileId = "f2", Counters = new ClaudeCounters(100, 10, null, null, null, null) };
        var result = new ClaudeAuditAdapter().Reconstruct([partial, rich], SourceFixture.Scope());
        AuditCanonicalJson
            .ContentHash(AuditCanonicalJson.Serialize(result))
            .Should()
            .Be("74233e2752e1e0b67c5b80d6268aff9b5b2122defcec00c80eda62a72ccbd5b6");
        result.CompatibleGroups.Single().Key.Pricing.Context.Should().BeNull();
    }

    [Theory]
    [InlineData(100000, 0, 0, "prompt_le_100000")]
    [InlineData(100001, 0, 0, "prompt_gt_100000")]
    [InlineData(70000, 30001, 0, "prompt_gt_100000")]
    [InlineData(70000, 20000, 10000, "prompt_le_100000")]
    public void VersionTwo_IndependentlyDerivesBoundaryAndPreservesCapacity(
        long input,
        long read,
        long creation,
        string context
    )
    {
        var record = Prospective(input, read, creation) with
        {
            Pricing = SourceFixture.Pricing with { ContextTokens = 1000000 },
        };
        var group = new ClaudeAuditAdapter()
            .Reconstruct([record], SourceFixture.Scope())
            .CompatibleGroups.Should()
            .ContainSingle()
            .Subject;
        group.Key.Pricing.Context.Should().Be(context);
        group.Key.Pricing.ContextTokens.Should().Be(1000000);
        group.Tokens.CacheWriteTokens.Should().Be(creation);
    }

    [Fact]
    public void VersionTwo_MixedBandsWithOtherwiseEqualDimensionsKeepOwnOutput()
    {
        var lower = Prospective(60000, 0, 0);
        var secondLower = lower with { MessageId = "m2", Sequence = 2 };
        var upper = Prospective(120000, 0, 0) with
        {
            MessageId = "m3",
            Sequence = 3,
            Counters = new ClaudeCounters(120000, 37, 0, 0, null, null),
        };
        var groups = new ClaudeAuditAdapter()
            .Reconstruct([lower, lower with { FileId = "copy" }, secondLower, upper], SourceFixture.Scope())
            .CompatibleGroups;
        groups.Should().HaveCount(2);
        groups
            .Single(g => g.Key.Pricing.Context == "prompt_le_100000")
            .Tokens.Should()
            .Be(new UsageCounters(120000, 40, 0, 0, null, null));
        groups.Single(g => g.Key.Pricing.Context == "prompt_gt_100000").Tokens.OutputTokens.Should().Be(37);
    }

    [Theory]
    [InlineData("missing_read")]
    [InlineData("missing_creation")]
    [InlineData("inconsistent_subsets")]
    [InlineData("partial_subset_above_total")]
    [InlineData("overflow")]
    [InlineData("missing_identity")]
    public void VersionTwo_UnprovenPromptStaysUnknown(string fault)
    {
        var record = Prospective(70000, 20000, 10001) with
        {
            Pricing = SourceFixture.Pricing with { Context = "prompt_le_100000" },
        };
        record = fault switch
        {
            "missing_read" => record with { Counters = new ClaudeCounters(70000, 20, 10001, null, null, null) },
            "missing_creation" => record with { Counters = new ClaudeCounters(70000, 20, null, 20000, 1, null) },
            "inconsistent_subsets" => record with { Counters = new ClaudeCounters(70000, 20, 10001, 20000, 1, 1) },
            "partial_subset_above_total" => record with
            {
                Counters = new ClaudeCounters(70000, 20, 10001, 20000, 10002, null),
            },
            "overflow" => record with { Counters = new ClaudeCounters(long.MaxValue, 20, 1, 1, null, null) },
            _ => record with { MessageId = null },
        };
        var result = new ClaudeAuditAdapter().Reconstruct([record], SourceFixture.Scope());
        result.CompatibleGroups.Single().Key.Pricing.Context.Should().BeNull();
        result.Coverage.Gaps.Should().Contain(g => g.DiagnosticCode == "claude_prompt_context_unknown");
    }

    [Fact]
    public void VersionTwo_SplitOnlyCreationIsCountedOnce()
    {
        var record = Prospective(70000, 20000, 0) with
        {
            Counters = new ClaudeCounters(70000, 20, null, 20000, 5000, 5001),
        };
        var group = new ClaudeAuditAdapter().Reconstruct([record], SourceFixture.Scope()).CompatibleGroups.Single();
        group.Key.Pricing.Context.Should().Be("prompt_gt_100000");
        group.Tokens.CacheWriteTokens.Should().Be(10001);
    }

    [Theory]
    [InlineData(5000, "prompt_le_100000")]
    [InlineData(5001, "prompt_gt_100000")]
    public void VersionTwo_TotalAndSplitsAreCountedOnceAndOutputDoesNotAffectBand(long hour, string context)
    {
        var record = Prospective(70000, 20000, 5000 + hour) with
        {
            Counters = new ClaudeCounters(70000, 200001, 5000 + hour, 20000, 5000, hour),
        };
        var group = new ClaudeAuditAdapter().Reconstruct([record], SourceFixture.Scope()).CompatibleGroups.Single();
        group.Key.Pricing.Context.Should().Be(context);
        group.Tokens.CacheWriteTokens.Should().Be(5000 + hour);
        group.Tokens.OutputTokens.Should().Be(200001);
    }

    [Theory]
    [InlineData("agree")]
    [InlineData("input")]
    [InlineData("creation")]
    [InlineData("model")]
    [InlineData("tier")]
    [InlineData("speed")]
    [InlineData("geo")]
    [InlineData("subset")]
    public void VersionTwo_CompleteCandidateRequiresAllCopyAgreement(string change)
    {
        var complete = Prospective(70000, 20000, 10001);
        var copy = complete with { FileId = "copy", Counters = new ClaudeCounters(70000, 1, null, null, null, null) };
        copy = change switch
        {
            "input" => copy with { Counters = new ClaudeCounters(70001, 1, null, null, null, null) },
            "creation" => copy with { Counters = new ClaudeCounters(70000, 1, 10002, null, null, null) },
            "model" => copy with { Model = "claude-haiku-5-5-20261020" },
            "tier" => copy with { Pricing = copy.Pricing with { SourceServiceTier = "priority" } },
            "speed" => copy with { Pricing = copy.Pricing with { Speed = "fast" } },
            "geo" => copy with { Pricing = copy.Pricing with { InferenceGeo = "us" } },
            "subset" => copy with { Counters = new ClaudeCounters(70000, 1, null, null, 10002, null) },
            _ => copy,
        };
        var result = new ClaudeAuditAdapter().Reconstruct([copy, complete], SourceFixture.Scope());
        result.CompatibleGroups.Single().Key.Pricing.Context.Should().Be(change == "agree" ? "prompt_gt_100000" : null);
        result
            .Coverage.Gaps.Any(g => g.DiagnosticCode == "claude_prompt_context_unknown")
            .Should()
            .Be(change != "agree");
    }

    [Fact]
    public void VersionTwo_DisjointPartialCopiesNeverManufactureCompleteness()
    {
        var first = Prospective(70000, 0, 0) with { Counters = new ClaudeCounters(70000, 20, null, null, null, null) };
        var second = first with { FileId = "f2", Counters = new ClaudeCounters(null, 20, 10001, 20000, null, null) };
        var result = new ClaudeAuditAdapter().Reconstruct([first, second], SourceFixture.Scope());
        result.Coverage.Gaps.Should().Contain(g => g.DiagnosticCode == "claude_prompt_context_unknown");
        result.CompatibleGroups.Should().OnlyContain(g => g.Key.Pricing.Context == null);
    }

    [Theory]
    [InlineData("claude-sonnet")]
    [InlineData("claude-haiku-5-50")]
    [InlineData("claude-haiku-5-5-20260230")]
    [InlineData("claude-haiku-5-5-00000101")]
    public void VersionTwo_UnqualifiedFlatModelsNeverReceiveDerivedContext(string model)
    {
        var record = Prospective(120000, 0, 0) with
        {
            Model = model,
            Pricing = SourceFixture.Pricing with { Context = "prompt_gt_100000" },
        };
        var result = new ClaudeAuditAdapter().Reconstruct([record], SourceFixture.Scope());
        result.CompatibleGroups.Single().Key.Pricing.Context.Should().BeNull();
        result.Coverage.Gaps.Should().BeEmpty();
    }

    [Fact]
    public void MixedEvidenceVersionsForSameMessageAreAmbiguousAndOmitted()
    {
        var record = Prospective(100001, 0, 0);
        var result = new ClaudeAuditAdapter().Reconstruct(
            [record, record with { SourceVersion = "1", FileId = "old" }],
            SourceFixture.Scope()
        );
        result.CompatibleGroups.Should().BeEmpty();
        result.Coverage.Gaps.Should().Contain(g => g.DiagnosticCode == "mixed_source_versions");
    }

    [Theory]
    [InlineData(-1, -1, false)]
    [InlineData(-1, -1, true)]
    [InlineData(2, 2, false)]
    [InlineData(2, 2, true)]
    [InlineData(-1, 2, false)]
    [InlineData(-1, 2, true)]
    public void MixedVersionsWhollyOutsideScopeDoNotChangeReconstructionOrChecks(
        int firstDay,
        int secondDay,
        bool reverse
    )
    {
        var record = SourceFixture.Claude();
        var first = record with
        {
            FileId = "outside-v1",
            MessageId = "outside",
            Timestamp = record.Timestamp.AddDays(firstDay),
        };
        var second = first with
        {
            FileId = "outside-v2",
            SourceVersion = "2",
            Timestamp = record.Timestamp.AddDays(secondDay),
        };
        SourceUsageRecord[] records = reverse ? [record, second, first] : [record, first, second];
        var adapter = new ClaudeAuditAdapter();
        var control = adapter.Reconstruct([record], SourceFixture.Scope());
        control.CompatibleGroups.Should().ContainSingle();
        control.Coverage.Gaps.Should().BeEmpty();
        AuditCanonicalJson
            .Serialize(adapter.Reconstruct(records, SourceFixture.Scope()))
            .Should()
            .Equal(AuditCanonicalJson.Serialize(control));

        var input = SourceFixture.Input(record);
        input = input with
        {
            Captures =
            [
                input.Captures[0] with
                {
                    SourceFormats =
                    [
                        new(record.SourceId, record.SourceFormat, "1"),
                        new(record.SourceId, record.SourceFormat, "2"),
                    ],
                },
            ],
            Events = [SourceFixture.Event(record, new(100, 20, 60, 40, 10, null))],
        };
        var check = new SourceAccountingCheck();
        var withOutside = input with
        {
            Records = records,
            Captures = [input.Captures[0] with { RecordCount = records.Length }],
        };
        var matched = check.Evaluate(input);
        matched.Verdict.Should().Be(CheckVerdict.Matched);
        AuditCanonicalJson.Serialize(check.Evaluate(withOutside)).Should().Equal(AuditCanonicalJson.Serialize(matched));
        var daily = new DailyAttributionCheck();
        var dailyControl = daily.Evaluate(input);
        dailyControl.Verdict.Should().Be(CheckVerdict.Matched);
        AuditCanonicalJson
            .Serialize(daily.Evaluate(withOutside))
            .Should()
            .Equal(AuditCanonicalJson.Serialize(dailyControl));

        var missing = input with { Events = [] };
        var missingControl = check.Evaluate(missing);
        missingControl.Verdict.Should().Be(CheckVerdict.Mismatched);
        missingControl.Findings.Should().Contain(f => f.Field == "groupPresence");
        AuditCanonicalJson
            .Serialize(check.Evaluate(withOutside with { Events = [] }))
            .Should()
            .Equal(AuditCanonicalJson.Serialize(missingControl));
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(0, 0, true)]
    [InlineData(-1, 0, false)]
    [InlineData(-1, 0, true)]
    [InlineData(1, 2, false)]
    [InlineData(1, 2, true)]
    public void MixedVersionsWithAnyCopyInsideScopeRemainAmbiguous(int firstDay, int secondDay, bool reverse)
    {
        var record = Prospective(100001, 0, 0);
        var first = record with { Timestamp = record.Timestamp.AddDays(firstDay) };
        var second = record with
        {
            SourceVersion = "1",
            FileId = "old",
            Timestamp = record.Timestamp.AddDays(secondDay),
        };
        var result = new ClaudeAuditAdapter().Reconstruct(
            reverse ? [second, first] : [first, second],
            SourceFixture.Scope()
        );
        result.CompatibleGroups.Should().BeEmpty();
        result.ChronologicalGroups.Should().BeEmpty();
        result.Coverage.Gaps.Should().ContainSingle().Which.DiagnosticCode.Should().Be("mixed_source_versions");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("default")]
    [InlineData("unparseable")]
    [InlineData("non-utc")]
    public void MixedVersionsCannotUseInvalidTimestampAsOutsideScopeProof(string fault)
    {
        var record = SourceFixture.Claude() with { Timestamp = SourceFixture.Claude().Timestamp.AddDays(-1) };
        var node = JsonNode.Parse(AuditCanonicalJson.Serialize(record with { SourceVersion = "2", FileId = "other" }))!;
        if (fault == "missing")
        {
            node.AsObject().Remove("timestamp");
        }
        else
        {
            node["timestamp"] = fault switch
            {
                "default" => "0001-01-01T00:00:00Z",
                "non-utc" => "2026-10-19T23:00:00+01:00",
                _ => "invalid",
            };
        }
        var action = () =>
            new ClaudeAuditAdapter().Reconstruct(
                [
                    record,
                    AuditCanonicalJson.Deserialize<SourceUsageRecord>(
                        System.Text.Encoding.UTF8.GetBytes(node.ToJsonString())
                    ),
                ],
                SourceFixture.Scope()
            );
        action.Should().Throw<AuditValidationException>();
    }

    [Fact]
    public void MixedVersionsAtExactNativePositionAreRejectedByExistingWireIdentity()
    {
        var record = Prospective(100001, 0, 0);
        var action = () =>
            new ClaudeAuditAdapter().Reconstruct([record, record with { SourceVersion = "1" }], SourceFixture.Scope());
        action.Should().Throw<AuditValidationException>().Which.Code.Should().Be("conflicting_identity");
    }

    [Fact]
    public void WrongProductionContextIsVisibleThroughIndependentSourceComparison()
    {
        var pricing = Checks.PricingArithmeticCheckTests.Fixture("Anthropic", 100001, 20, 0, 0, 0);
        var wrong = pricing.Events[0].PricingApplication!.Dimensions with { Context = "prompt_le_100000" };
        var record = Prospective(100001, 0, 0) with { Pricing = wrong with { Context = null } };
        var input = SourceFixture.Input(record);
        var e = SourceFixture.Event(record, new(100001, 20, 0, 0, null, null));
        input = input with
        {
            Scope = input.Scope with { RequestedChecks = [CheckCode.SourceAccounting, CheckCode.PricingArithmetic] },
            Captures = [input.Captures[0] with { SourceFormats = [new(record.SourceId, record.SourceFormat, "2")] }],
            PricingCatalogs =
            [
                pricing.PricingCatalogs[0] with
                {
                    Rates = pricing
                        .PricingCatalogs[0]
                        .Rates.Select(r => r with { Model = record.Model!, Dimensions = wrong })
                        .ToArray(),
                },
            ],
            Events =
            [
                e with
                {
                    CostUsd = 0.200402m,
                    CacheDurations = new(0, 0),
                    SourceDimensions = wrong,
                    PricingApplication = pricing.Events[0].PricingApplication! with { Dimensions = wrong },
                },
            ],
        };
        var result = new AuditVerifier().Verify(input);
        result.Checks.Single(c => c.Code == CheckCode.SourceAccounting).Verdict.Should().Be(CheckVerdict.Mismatched);
        result
            .Findings.Should()
            .Contain(f => f.RuleCode == "source_accounting_difference" && f.Field == "groupPresence");
        result.Checks.Single(c => c.Code == CheckCode.PricingArithmetic).Coverage.ComparedGroups.Should().Be(1);
        result.Findings.Should().NotContain(f => f.RuleCode == "pricing_cost_difference");
    }

    internal static SourceUsageRecord Prospective(long input, long read, long creation) =>
        SourceFixture.Claude() with
        {
            SourceVersion = "2",
            Model = "claude-haiku-5-5",
            Pricing = SourceFixture.Pricing with
            {
                SourceServiceTier = "standard",
                Speed = "standard",
                InferenceGeo = "global",
            },
            Counters = new ClaudeCounters(input, 20, creation, read, null, null),
        };

    [Theory]
    [InlineData("CLAUDE-HAIKU-5-5")]
    [InlineData("claude-haiku-5-5-20260228")]
    [InlineData("claude-haiku-5-5-20280229")]
    public void VersionTwo_QualifiedNamesDeriveContextAndIgnoreCompanionClaim(string model)
    {
        var record = Prospective(100001, 0, 0) with
        {
            Model = model,
            Pricing = SourceFixture.Pricing with { Context = "prompt_le_100000" },
        };
        new ClaudeAuditAdapter()
            .Reconstruct([record], SourceFixture.Scope())
            .CompatibleGroups.Single()
            .Key.Pricing.Context.Should()
            .Be("prompt_gt_100000");
    }

    [Fact]
    public void UnknownContextInAnotherGroupDoesNotMaskKnownSourceNumericError()
    {
        var known = Prospective(100001, 0, 0);
        var unknown = known with
        {
            Sequence = 2,
            MessageId = "m2",
            Counters = new ClaudeCounters(500, 20, null, 0, null, null),
        };
        var input = SourceFixture.Input(known, unknown);
        var dimensions = known.Pricing with { Context = "prompt_gt_100000" };
        var e = SourceFixture.Event(known, new(100002, 20, 0, 0, null, null));
        input = input with
        {
            Captures = [input.Captures[0] with { SourceFormats = [new(known.SourceId, known.SourceFormat, "2")] }],
            Events = [e with { SourceDimensions = dimensions }],
        };
        var result = new AuditVerifier().Verify(input);
        result.Checks.Single(c => c.Code == CheckCode.SourceAccounting).Verdict.Should().Be(CheckVerdict.Mismatched);
        result
            .Findings.Should()
            .Contain(f => f.Field == "inputTokens" && f.Expected!.Value == "100001" && f.Actual!.Value == "100002");
        result.Coverage.Gaps.Should().Contain(g => g.DiagnosticCode == "claude_prompt_context_unknown");
    }

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
