using AiObservatory.Audit.Checks;
using AiObservatory.Audit.Contracts;
using AwesomeAssertions;

namespace AiObservatory.Audit.Tests.Checks;

public sealed class EventAggregationCheckTests
{
    [Fact]
    public void NullOptionalTokens_NegativeSavings_AndCorrectedPosting_MatchStoredFields()
    {
        var row = CheckFixture.Event() with { CorrectedAt = CheckFixture.Event().ObservedAt };
        var input = CheckFixture.Input(row) with { Aggregates = [CheckFixture.Aggregate()] };
        var result = new EventAggregationCheck().Evaluate(input);
        result.Verdict.Should().Be(CheckVerdict.Matched);
        result.Coverage.ComparedGroups.Should().Be(1);
        result.Coverage.Exclusions.Should().Contain(e => e.Field == "thoughtTokens");
        result.Findings.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false, "inputTokens", "1")]
    [InlineData(true, "requestCount", "-1")]
    public void OneTokenOrDistinctAppendOnlyEvent_IsAnExactMismatch(bool duplicate, string field, string delta)
    {
        var row = CheckFixture.Event();
        var input = CheckFixture.Input(row) with { Aggregates = [CheckFixture.Aggregate()] };
        input = duplicate
            ? input with
            {
                Events = [row, CheckFixture.Event(2)],
            }
            : input with
            {
                Aggregates = [CheckFixture.Aggregate() with { InputTokens = 11 }],
            };
        var result = new EventAggregationCheck().Evaluate(input);
        result.Verdict.Should().Be(CheckVerdict.Mismatched);
        result
            .Findings.Should()
            .Contain(f => f.Field == field && f.Delta == new AuditValue(AuditValueKind.Integer, delta));
    }

    [Fact]
    public void IdenticalRowCopies_AreDeduplicated()
    {
        var row = CheckFixture.Event();
        var result = new EventAggregationCheck().Evaluate(
            CheckFixture.Input(row, row) with
            {
                Aggregates = [CheckFixture.Aggregate()],
            }
        );
        result.Verdict.Should().Be(CheckVerdict.Matched);
    }

    [Fact]
    public void ZeroTombstones_NoneAndUnknownMoney_KeepRequestsAndUnknownCounts()
    {
        var unknown = CheckFixture.Event() with
        {
            Tokens = new(0, 0, null, null, null, null),
            CostUsd = null,
            CacheSavingsUsd = null,
        };
        var none = unknown with
        {
            RowId = CheckFixture.Event(2).RowId,
            Provenance = unknown.Provenance with { CostBasis = AccountingBasis.None },
        };
        var aggregate = CheckFixture.Aggregate() with
        {
            InputTokens = 0,
            OutputTokens = 0,
            CacheWriteTokens = 0,
            CostUsd = 0,
            CacheSavingsUsd = 0,
            UnknownCostCount = 1,
            UnknownCacheSavingsCount = 1,
        };
        var result = new EventAggregationCheck().Evaluate(
            CheckFixture.Input(unknown, none) with
            {
                Aggregates =
                [
                    aggregate,
                    aggregate with
                    {
                        Key = aggregate.Key with { CostBasis = AccountingBasis.None },
                        UnknownCostCount = 0,
                        UnknownCacheSavingsCount = 0,
                    },
                ],
            }
        );
        result.Verdict.Should().Be(CheckVerdict.Matched);
        result.Coverage.ComparedGroups.Should().Be(2);
    }

    [Theory]
    [InlineData("date")]
    [InlineData("provider")]
    [InlineData("model")]
    [InlineData("source")]
    [InlineData("kind")]
    [InlineData("scope")]
    [InlineData("basis")]
    public void EveryGroupingDimension_PreservesSeparatePostings(string dimension)
    {
        var first = CheckFixture.Event();
        var second = CheckFixture.Event(2);
        var key = CheckFixture.Aggregate().Key;
        (second, key) = dimension switch
        {
            "date" => (
                second with
                {
                    OccurredAt = second.OccurredAt.AddHours(2),
                },
                key with
                {
                    Date = key.Date.AddDays(1),
                }
            ),
            "provider" => (second with { Provider = "other" }, key with { Provider = "other" }),
            "model" => (second with { Model = "model" }, key with { Model = "model" }),
            "source" => (second with { SourceId = "other" }, key with { SourceId = "other" }),
            "kind" => (
                second with
                {
                    Provenance = second.Provenance with { SourceKind = SourceKind.Manual },
                },
                key with
                {
                    SourceKind = SourceKind.Manual,
                }
            ),
            "scope" => (
                second with
                {
                    Provenance = second.Provenance with { UsageScope = UsageScope.Api },
                },
                key with
                {
                    UsageScope = UsageScope.Api,
                }
            ),
            _ => (
                second with
                {
                    Provenance = second.Provenance with { CostBasis = AccountingBasis.ProviderEstimated },
                },
                key with
                {
                    CostBasis = AccountingBasis.ProviderEstimated,
                }
            ),
        };
        var input = CheckFixture.Input(first, second);
        input = input with
        {
            Scope = input.Scope with
            {
                Sources = [new(CheckFixture.Machine, "local"), new(CheckFixture.Machine, "other")],
            },
            Aggregates = [CheckFixture.Aggregate(), CheckFixture.Aggregate() with { Key = key }],
        };
        var result = new EventAggregationCheck().Evaluate(input);
        result.Verdict.Should().Be(CheckVerdict.Matched);
        result.Coverage.ComparedGroups.Should().Be(2);
    }

    [Fact]
    public void MissingZeroOnlyPosting_IsStillAMismatch()
    {
        var row = CheckFixture.Event() with { Tokens = new(0, 0, 0, 0, 0, null), CostUsd = 0, CacheSavingsUsd = 0 };
        var outcome = new EventAggregationCheck().Evaluate(CheckFixture.Input(row));
        outcome.Verdict.Should().Be(CheckVerdict.Mismatched);
        outcome
            .Findings.Should()
            .Contain(f => f.Field == "requestCount" && f.Delta == new AuditValue(AuditValueKind.Integer, "-1"));
    }

    [Fact]
    public void EmptyEvidence_IsIncomplete()
    {
        new EventAggregationCheck().Evaluate(CheckFixture.Input()).Verdict.Should().Be(CheckVerdict.Incomplete);
    }

    [Fact]
    public void CountsAboveJavascriptPrecision_AreComparedExactly_AndOverflowIsSanitized()
    {
        var row = CheckFixture.Event() with { Tokens = new(9_007_199_254_740_993, 20, null, 3, null, null) };
        var input = CheckFixture.Input(row) with
        {
            Aggregates = [CheckFixture.Aggregate() with { InputTokens = 9_007_199_254_740_992 }],
        };
        new EventAggregationCheck()
            .Evaluate(input)
            .Findings.Should()
            .Contain(f =>
                f.Field == "inputTokens"
                && f.Expected == new AuditValue(AuditValueKind.Integer, "9007199254740993")
                && f.Delta == new AuditValue(AuditValueKind.Integer, "-1")
            );
        var max = row with { Tokens = row.Tokens with { InputTokens = long.MaxValue } };
        var overflow = () => new EventAggregationCheck().Evaluate(CheckFixture.Input(max, CheckFixture.Event(2)));
        overflow.Should().Throw<AuditValidationException>().WithMessage("counter_overflow");
    }

    [Fact]
    public void StoredMoneyUsesNamedColumnPrecision_WhileTokensRemainExact()
    {
        var input = CheckFixture.Input(CheckFixture.Event()) with
        {
            Aggregates = [CheckFixture.Aggregate() with { CostUsd = 1.254m }],
            Tolerances = [new("eventAggregation.costUsd", ValueUnit.Money, "USD", 2, 0.005m)],
        };
        new EventAggregationCheck().Evaluate(input).Verdict.Should().Be(CheckVerdict.Matched);
        new EventAggregationCheck()
            .Evaluate(input with { Tolerances = [] })
            .Findings.Should()
            .Contain(f => f.Field == "costUsd" && f.Delta == new AuditValue(AuditValueKind.Decimal, "0.004"));
        new EventAggregationCheck()
            .Evaluate(input with { Aggregates = [input.Aggregates[0] with { InputTokens = 11 }] })
            .Verdict.Should()
            .Be(CheckVerdict.Mismatched);
    }

    [Fact]
    public void ScopedWindowAndSourceFilters_AreIdenticalForBothSides()
    {
        var row = CheckFixture.Event();
        var outside = CheckFixture.Event(2) with { SourceId = "other" };
        var input = CheckFixture.Input(
            row,
            outside,
            CheckFixture.Event(3) with
            {
                OccurredAt = row.OccurredAt.AddDays(-2),
            }
        ) with
        {
            Aggregates =
            [
                CheckFixture.Aggregate(),
                CheckFixture.Aggregate() with
                {
                    Key = CheckFixture.Aggregate().Key with { SourceId = "other" },
                    InputTokens = 999,
                },
            ],
        };
        var result = new EventAggregationCheck().Evaluate(input);
        result.Verdict.Should().Be(CheckVerdict.Matched);
        result.Coverage.ComparedGroups.Should().Be(1);
        result.Coverage.Exclusions.Should().Contain(e => e.Field == "machineAttribution");
    }
}
