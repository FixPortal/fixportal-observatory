using AiObservatory.Audit.Checks;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using AiObservatory.Audit.Sources;
using AiObservatory.Audit.Tests.Checks;
using AiObservatory.Audit.Tests.Sources;
using AwesomeAssertions;

namespace AiObservatory.Audit.Tests;

public sealed class ReviewBatch64Tests
{
    [Fact]
    public void NullAndUnknownModelsSharePresentationCacheEligibility()
    {
        var cached = CheckFixture.Event() with { CacheSavingsUsd = 1m };
        var uncached = CheckFixture.Event(2) with
        {
            Model = "unknown",
            Tokens = new(10, 20, 0, 0, 0, null),
            CacheSavingsUsd = null,
        };
        var values = PresentationTotalsCheck.Reference(CheckFixture.Input(cached, uncached));
        values.Single(v => v.Field == "unknownCacheSavingsObservations").UnroundedValue.Should().Be(1);
        values.Single(v => v.Field == "cacheSavingsUsd").UnroundedValue.Should().Be(1m);
    }

    [Theory]
    [InlineData(1000, 200, 0, "0.0042")]
    [InlineData(100, 200, 0, "0.0024")]
    [InlineData(100, 200, 40, "0.0025")]
    public void FrozenOpenAiInputIsUncachedAndWritesUseCatalogMeter(long input, long read, long write, string expected)
    {
        var result = new AuditVerifier().Verify(PricingArithmeticCheckTests.Fixture("OpenAi", input, 100, read, write));
        result.Findings.Single(f => f.Field == "costUsd").Expected!.Value.Should().Be(expected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullAndLiteralUnknownShareTheProductionAggregateKey(bool corrupt)
    {
        var aggregate = CheckFixture.Aggregate() with
        {
            Key = CheckFixture.Aggregate().Key with { Model = "unknown" },
            InputTokens = corrupt ? 21 : 20,
            OutputTokens = 40,
            CacheWriteTokens = 6,
            CostUsd = 2.5m,
            CacheSavingsUsd = -0.5m,
            RequestCount = 2,
        };
        var input = CheckFixture.Input(CheckFixture.Event(), CheckFixture.Event(2) with { Model = "unknown" }) with
        {
            Aggregates = [aggregate],
        };
        var result = new EventAggregationCheck().Evaluate(input);
        result.Verdict.Should().Be(corrupt ? CheckVerdict.Mismatched : CheckVerdict.Matched);
        result.Findings.Should().HaveCount(corrupt ? 1 : 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingRequiredCounterDoesNotTurnPartialSubtotalIntoDrift(bool independentMismatch)
    {
        var complete = SourceFixture.Claude();
        var missing = SourceFixture.Claude(2) with
        {
            MessageId = "m2",
            Counters = new ClaudeCounters(null, 20, 40, 60, 30, 10),
        };
        var other = SourceFixture.Claude(3) with { MessageId = "m3", Model = "other" };
        var input = SourceFixture.Input(complete, missing, other) with
        {
            Events =
            [
                SourceFixture.Event(complete, new(200, 40, 120, 80, 20, null)),
                SourceFixture.Event(other, new(independentMismatch ? 101 : 100, 20, 60, 40, 10, null)),
            ],
        };
        var result = new SourceAccountingCheck().Evaluate(input);
        result.Verdict.Should().Be(independentMismatch ? CheckVerdict.Mismatched : CheckVerdict.Incomplete);
        result
            .Findings.Where(f => f.Expected != null && f.Actual != null)
            .Should()
            .HaveCount(independentMismatch ? 1 : 0);
        result.Coverage.Gaps.Should().Contain(g => g.DiagnosticCode == "missing_counter");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StraddlingCodexFinalOutsideWindowRetainsClosureQualification(bool closed)
    {
        var basis = SourceFixture.Codex() with
        {
            Kind = UsageRecordKind.Baseline,
            Timestamp = new(2026, 10, 19, 23, 0, 0, TimeSpan.Zero),
            Counters = new CodexCounters(0, 0, 0, 0, 0),
            Closure = new(ClosureState.Unknown, null, null),
        };
        var inside = basis with
        {
            Sequence = 2,
            Kind = UsageRecordKind.CumulativeUsage,
            Timestamp = basis.Timestamp.AddDays(1),
            Counters = new CodexCounters(100, 20, 10, 0, 110),
        };
        var after = inside with
        {
            Sequence = 3,
            Timestamp = inside.Timestamp.AddDays(2),
            Counters = new CodexCounters(200, 40, 20, 0, 220),
            Closure = closed ? new(ClosureState.ExplicitlyClosed, inside.Timestamp.AddDays(2), 3) : inside.Closure,
        };
        var result = new CodexAuditAdapter().Reconstruct([basis, inside, after], SourceFixture.Scope("codex-local"));
        result.ChronologicalGroups.Should().ContainSingle().Which.Provisional.Should().Be(!closed);
        result.Coverage.Gaps.Any(g => g.DiagnosticCode == "missing_closure").Should().Be(!closed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoSelectedPricingObservationsAreIncomplete(bool outside)
    {
        var input = PricingArithmeticCheckTests.Fixture();
        input = input with
        {
            Events = outside ? [input.Events[0] with { OccurredAt = input.Events[0].OccurredAt.AddDays(-10) }] : [],
        };
        var result = new PricingArithmeticCheck().Evaluate(input);
        result.Verdict.Should().Be(CheckVerdict.Incomplete);
        result.Coverage.Gaps.Should().Contain(g => g.DiagnosticCode == "no_pricing_evidence");
        new PricingArithmeticCheck()
            .Evaluate(
                input with
                {
                    Scope = input.Scope with
                    {
                        RequestedChecks = [CheckCode.EventAggregation],
                        ExcludedChecks = [CheckCode.PricingArithmetic],
                    },
                }
            )
            .Verdict.Should()
            .Be(CheckVerdict.Excluded);
    }

    [Fact]
    public void FreshSourceFixturesHaveRepeatableCanonicalIdentityAndDistinctDomains()
    {
        static AuditInput Build()
        {
            var r = SourceFixture.Claude();
            return SourceFixture.Input(r) with { Events = [SourceFixture.Event(r, new(100, 20, 60, 40, 10, null))] };
        }
        var first = Build();
        AuditCanonicalJson.Serialize(first).Should().Equal(AuditCanonicalJson.Serialize(Build()));
        first.Captures[0].CaptureId.Should().NotBe(first.Captures[0].RequestId);
        first
            .Events[0]
            .RowId.Should()
            .NotBe(SourceFixture.Event(SourceFixture.Claude(2), first.Events[0].Tokens).RowId);
    }
}
