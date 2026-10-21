using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using AiObservatory.Audit.Sources;
using AwesomeAssertions;

namespace AiObservatory.Audit.Tests.Sources;

public sealed class CodexAuditAdapterTests
{
    [Fact]
    public void CumulativeTotals_CrossingMidnight_PreserveFinalDayCompatibilityAndObservedDeltas()
    {
        var first = SourceFixture.Codex();
        var later = first with
        {
            Sequence = 2,
            Timestamp = first.Timestamp.AddHours(2),
            Counters = new CodexCounters(180, 100, 30, 9, 210),
        };
        var adapter = new CodexAuditAdapter();
        var result = adapter.Reconstruct([later, first], SourceFixture.Scope("codex-local"));
        var compatible = result.CompatibleGroups.Should().ContainSingle().Subject;
        compatible.Key.Date.Should().Be(new DateOnly(2026, 10, 21));
        compatible.Tokens.Should().Be(new UsageCounters(80, 30, 100, null, null, 9));
        result.ChronologicalGroups.Should().HaveCount(2);
        result.ChronologicalGroups[0].Tokens.Should().Be(new UsageCounters(40, 10, 60, null, null, 4));
        result.ChronologicalGroups[1].Tokens.Should().Be(new UsageCounters(40, 20, 40, null, null, 5));
        AuditCanonicalJson
            .Serialize(result)
            .Should()
            .Equal(
                AuditCanonicalJson.Serialize(adapter.Reconstruct([first, later], SourceFixture.Scope("codex-local")))
            );
    }

    [Fact]
    public void PreWindowBaseline_ProducesOnlyInWindowChronologicalIncrement()
    {
        var baseline = SourceFixture.Codex() with
        {
            Kind = UsageRecordKind.Baseline,
            Timestamp = new(2026, 10, 19, 23, 0, 0, TimeSpan.Zero),
        };
        var current = SourceFixture.Codex(2) with { Counters = new CodexCounters(180, 100, 30, 9, 210) };
        var result = new CodexAuditAdapter().Reconstruct([current, baseline], SourceFixture.Scope("codex-local"));
        result
            .ChronologicalGroups.Should()
            .ContainSingle()
            .Which.Tokens.Should()
            .Be(new UsageCounters(40, 20, 40, null, null, 5));
    }

    [Theory]
    [InlineData("baseline", "missing_baseline")]
    [InlineData("reset", "counter_reset")]
    [InlineData("switch", "uncertain_model_switch")]
    [InlineData("cache", "invalid_counter_subset")]
    [InlineData("counter", "missing_counter")]
    public void UncertainHistory_DoesNotInventChronologicalUsage(string fault, string diagnostic)
    {
        var first = SourceFixture.Codex();
        var later = first with
        {
            Sequence = 2,
            Timestamp = first.Timestamp.AddHours(1),
            Counters = new CodexCounters(180, 100, 30, 9, 210),
        };
        var records = fault switch
        {
            "baseline" => new[] { first with { Counters = new CodexCounters(100, 60, 10, 4, 110) } },
            "reset" => [first, later with { Counters = new CodexCounters(50, 20, 5, 2, 55) }],
            "switch" => [first, later with { Model = "gpt-6" }],
            "cache" => [first with { Counters = new CodexCounters(10, 60, 10, 4, 20) }],
            _ => [first with { Counters = new CodexCounters(null, 60, 10, 4, 110) }],
        };
        var result = new CodexAuditAdapter().Reconstruct(records, SourceFixture.Scope("codex-local"));
        result.Findings.Should().Contain(f => f.DiagnosticCode == diagnostic);
        result.Coverage.Gaps.Should().NotBeEmpty();
    }

    [Fact]
    public void GenerationAndSessionCopies_KeepDistinctSessionsButDedupeRepeatedObservation()
    {
        var first = SourceFixture.Codex();
        var result = new CodexAuditAdapter().Reconstruct(
            [first, first with { FileId = "copy" }, first with { Generation = "g2" }],
            SourceFixture.Scope("codex-local")
        );
        result.CompatibleGroups.Should().HaveCount(2);
        result.CompatibleGroups.Sum(g => g.Tokens.InputTokens).Should().Be(80);
    }

    [Theory]
    [InlineData("DEEPSEEK", "gpt-5", "DeepSeek", UsageScope.Api, AccountingBasis.ListPriceEstimate)]
    [InlineData(null, "glm-5", "Zai", UsageScope.Subscription, AccountingBasis.Notional)]
    [InlineData("OPENAI", "deepseek-chat", "OpenAI", UsageScope.Subscription, AccountingBasis.Notional)]
    public void ProviderFacts_PrecedeModelFallback(
        string? provider,
        string model,
        string expected,
        UsageScope scope,
        AccountingBasis basis
    )
    {
        var record = SourceFixture.Codex() with { Provider = provider, Model = model };
        var key = new CodexAuditAdapter()
            .Reconstruct([record], SourceFixture.Scope("codex-local"))
            .CompatibleGroups.Should()
            .ContainSingle()
            .Subject.Key;
        key.Provider.Should().Be(expected);
        key.Provenance.Should().Be(new ObservationProvenance(SourceKind.LocalTelemetry, scope, basis));
    }
}
