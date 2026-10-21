using AiObservatory.Audit.Contracts;

namespace AiObservatory.Audit.Sources;

public sealed class CodexAuditAdapter : ISourceAuditAdapter
{
    public SourceReconstruction Reconstruct(IReadOnlyList<SourceUsageRecord> records, AuditScope scope)
    {
        var facts = SourceAuditSupport.Prepare(records, scope, SourceTool.Codex);
        var compatible = new List<ReconstructedGroup>();
        var chronological = new List<ReconstructedGroup>();
        var findings = new List<AuditFinding>();
        foreach (var session in facts.GroupBy(SourceAuditSupport.Session))
        {
            ReconstructSession(session.ToArray(), scope, compatible, chronological, findings);
        }
        return SourceAuditSupport.Result(compatible, chronological, findings, facts);
    }

    private static void ReconstructSession(
        SourceUsageRecord[] session,
        AuditScope scope,
        List<ReconstructedGroup> compatible,
        List<ReconstructedGroup> chronological,
        List<AuditFinding> findings
    )
    {
        var observations = session
            .Where(r => r.Kind is UsageRecordKind.CumulativeUsage or UsageRecordKind.Baseline or UsageRecordKind.Reset)
            .OrderBy(r => r.Timestamp)
            .ThenBy(r => r.Sequence)
            .ThenBy(r => r.FileId, StringComparer.Ordinal)
            .DistinctBy(r => (r.Kind, r.Timestamp, r.Sequence, r.Model, r.Provider, r.Pricing, r.Counters))
            .ToArray();
        var chronologicalStart = chronological.Count;
        var state = new SessionState();
        foreach (var record in observations)
        {
            var findingCount = findings.Count;
            Observe(record, scope, state, chronological, findings);
            state.Provisional |= findings.Count > findingCount;
        }
        var finalRecord = state.FinalRecord;
        var finalTokens = state.FinalTokens;
        if (
            finalRecord is null
            || finalTokens is null
            || !SourceAuditSupport.InScope(SourceAuditSupport.Date(finalRecord), scope)
        )
        {
            return;
        }
        var closed = SourceAuditSupport.Closed(session, finalRecord);
        if (!closed)
        {
            findings.Add(SourceAuditSupport.Gap(finalRecord, "missing_closure"));
        }
        MarkProvisional(chronological, chronologicalStart, state.Provisional || !closed);
        var finalProvider = Provider(finalRecord);
        if (finalProvider is not null)
        {
            compatible.Add(
                new(
                    SourceAuditSupport.Key(finalRecord, finalProvider, Provenance(finalProvider)),
                    finalTokens,
                    finalRecord.Cost,
                    finalRecord.Currency,
                    state.Provisional || !closed,
                    observations.Select(SourceAuditSupport.Reference).Distinct().Order(StringComparer.Ordinal).ToArray()
                )
            );
        }
    }

    private static void MarkProvisional(List<ReconstructedGroup> groups, int start, bool provisional)
    {
        if (!provisional)
        {
            return;
        }
        for (var index = start; index < groups.Count; index++)
        {
            groups[index] = groups[index] with { Provisional = true };
        }
    }

    private static void Observe(
        SourceUsageRecord record,
        AuditScope scope,
        SessionState state,
        List<ReconstructedGroup> chronological,
        List<AuditFinding> findings
    )
    {
        if (record.Kind == UsageRecordKind.Reset)
        {
            findings.Add(SourceAuditSupport.Gap(record, "counter_reset"));
            state.Previous = null;
            state.PreviousRecord = null;
            state.Provisional = true;
            return;
        }
        var tokens = ReadObservation(record, findings);
        if (tokens is null)
        {
            state.Provisional = true;
            return;
        }
        var counters = (CodexCounters)record.Counters!;
        var provider = Provider(record);
        var switched = Switched(state.PreviousRecord, record);
        if (switched)
        {
            findings.Add(SourceAuditSupport.Gap(record, "uncertain_model_switch"));
            state.Provisional = true;
        }
        var delta = Increment(record, counters, state.Previous, findings);
        if (
            delta is not null
            && !switched
            && record.Kind != UsageRecordKind.Baseline
            && SourceAuditSupport.InScope(SourceAuditSupport.Date(record), scope)
            && provider is not null
        )
        {
            chronological.Add(
                new(
                    SourceAuditSupport.Key(record, provider, Provenance(provider)),
                    delta,
                    null,
                    null,
                    state.Provisional,
                    [SourceAuditSupport.Reference(record)]
                )
            );
        }
        state.Previous = counters;
        state.PreviousRecord = record;
        if (record.Kind != UsageRecordKind.Baseline)
        {
            state.FinalRecord = record;
            state.FinalTokens = tokens;
        }
    }

    private static UsageCounters? ReadObservation(SourceUsageRecord record, List<AuditFinding> findings)
    {
        if (!SourceAuditSupport.Supported(record))
        {
            findings.Add(SourceAuditSupport.Gap(record, "unsupported_source_format"));
            return null;
        }
        if (record.Counters is not CodexCounters counters)
        {
            findings.Add(SourceAuditSupport.Gap(record, "missing_counter"));
            return null;
        }
        if (Provider(record) is null)
        {
            findings.Add(SourceAuditSupport.Gap(record, "unknown_provider"));
        }
        if (record.Model is null)
        {
            findings.Add(SourceAuditSupport.Gap(record, "missing_model"));
        }
        return Normalize(record, counters, findings);
    }

    private static bool Switched(SourceUsageRecord? previous, SourceUsageRecord current) =>
        previous is not null
        && (
            previous.Model != current.Model
            || Provider(previous) != Provider(current)
            || previous.Pricing != current.Pricing
        );

    private sealed class SessionState
    {
        internal SourceUsageRecord? PreviousRecord { get; set; }
        internal CodexCounters? Previous { get; set; }
        internal SourceUsageRecord? FinalRecord { get; set; }
        internal UsageCounters? FinalTokens { get; set; }
        internal bool Provisional { get; set; }
    }

    private static UsageCounters? Normalize(
        SourceUsageRecord record,
        CodexCounters counters,
        List<AuditFinding> findings
    )
    {
        if (counters.InputTokens is null || counters.CachedInputTokens is null || counters.OutputTokens is null)
        {
            findings.Add(SourceAuditSupport.Gap(record, "missing_counter"));
            return null;
        }
        if (counters.ReasoningOutputTokens is null || counters.TotalTokens is null)
        {
            findings.Add(SourceAuditSupport.Gap(record, "missing_counter"));
        }
        if (counters.CachedInputTokens > counters.InputTokens || counters.ReasoningOutputTokens > counters.OutputTokens)
        {
            findings.Add(SourceAuditSupport.Gap(record, "invalid_counter_subset"));
            return null;
        }
        if (
            counters.TotalTokens is { } total
            && (decimal)counters.InputTokens.Value + counters.OutputTokens.Value != total
        )
        {
            findings.Add(SourceAuditSupport.Gap(record, "inconsistent_total_tokens"));
        }
        return new(
            counters.InputTokens.Value - counters.CachedInputTokens.Value,
            counters.OutputTokens.Value,
            counters.CachedInputTokens,
            null,
            null,
            counters.ReasoningOutputTokens
        );
    }

    private static UsageCounters? Increment(
        SourceUsageRecord record,
        CodexCounters current,
        CodexCounters? previous,
        List<AuditFinding> findings
    )
    {
        if (previous is null)
        {
            if (record.Kind == UsageRecordKind.Baseline)
            {
                return null;
            }
            if (!InitialUsage(current))
            {
                findings.Add(SourceAuditSupport.Gap(record, "missing_baseline"));
                return null;
            }
            return Normalize(record, current, findings);
        }
        if (Decreased(current, previous))
        {
            findings.Add(SourceAuditSupport.Gap(record, "counter_reset"));
            return null;
        }
        var delta = new CodexCounters(
            current.InputTokens - previous.InputTokens,
            current.CachedInputTokens - previous.CachedInputTokens,
            current.OutputTokens - previous.OutputTokens,
            current.ReasoningOutputTokens - previous.ReasoningOutputTokens,
            current.TotalTokens - previous.TotalTokens
        );
        if (
            current.LastTokenUsage is { } last
            && (
                last.InputTokens != delta.InputTokens
                || last.CachedInputTokens != delta.CachedInputTokens
                || last.OutputTokens != delta.OutputTokens
                || last.ReasoningOutputTokens != delta.ReasoningOutputTokens
                || last.TotalTokens != delta.TotalTokens
            )
        )
        {
            findings.Add(SourceAuditSupport.Gap(record, "last_usage_disagreement"));
        }
        return Normalize(record, delta, findings);
    }

    private static bool InitialUsage(CodexCounters counters) =>
        counters.LastTokenUsage is { } last
        && counters.InputTokens == last.InputTokens
        && counters.CachedInputTokens == last.CachedInputTokens
        && counters.OutputTokens == last.OutputTokens
        && counters.ReasoningOutputTokens == last.ReasoningOutputTokens
        && counters.TotalTokens == last.TotalTokens;

    private static bool Decreased(CodexCounters current, CodexCounters previous) =>
        current.InputTokens < previous.InputTokens
        || current.CachedInputTokens < previous.CachedInputTokens
        || current.OutputTokens < previous.OutputTokens
        || current.ReasoningOutputTokens < previous.ReasoningOutputTokens
        || current.TotalTokens < previous.TotalTokens;

    internal static string? Provider(SourceUsageRecord record)
    {
        if (record.Provider is not null)
        {
            if (record.Provider.Equals("openai", StringComparison.OrdinalIgnoreCase))
            {
                return "OpenAI";
            }
            if (record.Provider.Equals("zai", StringComparison.OrdinalIgnoreCase))
            {
                return "Zai";
            }
            if (record.Provider.Equals("deepseek", StringComparison.OrdinalIgnoreCase))
            {
                return "DeepSeek";
            }
            return null;
        }
        var model = record.Model;
        if (model is null)
        {
            return null;
        }
        if (model.StartsWith("glm-", StringComparison.OrdinalIgnoreCase))
        {
            return "Zai";
        }
        if (model.StartsWith("deepseek", StringComparison.OrdinalIgnoreCase))
        {
            return "DeepSeek";
        }
        string[] prefixes = ["gpt-", "o1", "o3", "o4", "codex"];
        return prefixes.Any(prefix => model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ? "OpenAI" : null;
    }

    private static ObservationProvenance Provenance(string provider) =>
        provider == "DeepSeek"
            ? new(SourceKind.LocalTelemetry, UsageScope.Api, AccountingBasis.ListPriceEstimate)
            : SourceAuditSupport.Subscription;
}
