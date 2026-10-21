using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;

namespace AiObservatory.Audit.Tests.Sources;

internal static class SourceFixture
{
    internal static readonly Guid Machine = Guid.Parse("00000000-0000-0000-0000-000000000001");
    internal static readonly DateOnly Day = new(2026, 10, 20);
    internal static readonly PricingDimensions Pricing = new(ServiceTier.Standard, false, null, null);

    internal static AuditScope Scope(string source = "claude-local") =>
        new(
            Day,
            Day.AddDays(1),
            [new(Machine, source)],
            [CheckCode.SourceAccounting, CheckCode.DailyAttribution],
            [],
            false
        );

    internal static SourceUsageRecord Claude(long sequence = 1) =>
        new()
        {
            MachineId = Machine,
            SourceId = "claude-local",
            SourceFormat = "claude-jsonl",
            SourceVersion = "1",
            Tool = SourceTool.Claude,
            Kind = UsageRecordKind.MessageUsage,
            Generation = "g1",
            FileId = "f1",
            SessionId = "s1",
            MessageId = "m1",
            Sequence = sequence,
            Timestamp = new(2026, 10, 20, 23, 0, 0, TimeSpan.Zero),
            Provider = "anthropic",
            Model = "claude-sonnet",
            Counters = LoadCounters<ClaudeCounters>("claude-rich.json"),
            Pricing = Pricing,
            Closure = new(ClosureState.ExplicitlyClosed, new(2026, 10, 21, 1, 0, 0, TimeSpan.Zero), 9),
        };

    internal static SourceUsageRecord Codex(long sequence = 1) =>
        Claude(sequence) with
        {
            SourceId = "codex-local",
            SourceFormat = "codex-jsonl",
            Tool = SourceTool.Codex,
            Kind = UsageRecordKind.CumulativeUsage,
            Model = "gpt-5",
            Provider = "openai",
            MessageId = null,
            Counters = LoadCounters<CodexCounters>("codex-initial.json"),
        };

    internal static AuditInput Input(params SourceUsageRecord[] records) =>
        new()
        {
            SchemaVersion = 1,
            VerifierVersion = "test-1",
            RuleVersion = "test-1",
            Scope = Scope(records.FirstOrDefault()?.SourceId ?? "claude-local"),
            Records = records,
            Captures = records
                .GroupBy(r => r.Generation)
                .Select(g => new CaptureManifest(
                    1,
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    Machine,
                    g.Key,
                    "test-1",
                    new(2026, 10, 22, 0, 0, 0, TimeSpan.Zero),
                    new(2026, 10, 22, 0, 1, 0, TimeSpan.Zero),
                    Scope(g.First().SourceId),
                    [new(g.First().SourceId, g.First().SourceFormat, "1")],
                    1,
                    1,
                    0,
                    0,
                    g.LongCount(),
                    Day,
                    Day.AddDays(1),
                    [],
                    [new string('a', 64)],
                    new(2026, 10, 22, 0, 2, 0, TimeSpan.Zero)
                ))
                .ToArray(),
        };

    internal static FrozenEvent Event(SourceUsageRecord record, UsageCounters counters) =>
        new(
            Guid.NewGuid(),
            null,
            record.SourceId,
            new(SourceKind.LocalTelemetry, UsageScope.Subscription, AccountingBasis.Notional),
            record.Timestamp,
            record.Timestamp,
            record.Timestamp,
            "Anthropic",
            record.Model,
            counters,
            null,
            null,
            null,
            record.SessionId,
            record.AgentId,
            null,
            new string('a', 64),
            new(
                "synthetic",
                new string('a', 64),
                "synthetic",
                record.Pricing,
                DateOnly.FromDateTime(record.Timestamp.UtcDateTime)
            )
        );

    private static T LoadCounters<T>(string name) =>
        AuditCanonicalJson.Deserialize<T>(
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sources", name))
        );
}
