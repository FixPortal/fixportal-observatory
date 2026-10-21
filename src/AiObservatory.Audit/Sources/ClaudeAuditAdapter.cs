using AiObservatory.Audit.Contracts;

namespace AiObservatory.Audit.Sources;

public sealed class ClaudeAuditAdapter : ISourceAuditAdapter
{
    public SourceReconstruction Reconstruct(IReadOnlyList<SourceUsageRecord> records, AuditScope scope)
    {
        var facts = SourceAuditSupport.Prepare(records, scope, SourceTool.Claude);
        var findings = new List<AuditFinding>();
        var groups = new List<ReconstructedGroup>();
        var sessions = facts.GroupBy(SourceAuditSupport.Session).ToDictionary(g => g.Key, g => g.ToArray());
        var messages = facts
            .Where(r => r.Kind == UsageRecordKind.MessageUsage)
            .GroupBy(r =>
                SourceAuditSupport.Identity(
                    r.MachineId,
                    r.SourceId,
                    r.Generation,
                    r.MessageId ?? SourceAuditSupport.Reference(r)
                )
            );
        foreach (var copies in messages)
        {
            var record = copies
                .OrderByDescending(Richness)
                .ThenByDescending(Completeness)
                .ThenBy(r => r.Sequence)
                .ThenBy(r => r.FileId, StringComparer.Ordinal)
                .First();
            if (!SourceAuditSupport.InScope(SourceAuditSupport.Date(record), scope))
            {
                continue;
            }
            if (!SourceAuditSupport.Supported(record))
            {
                findings.Add(SourceAuditSupport.Gap(record, "unsupported_source_format"));
                continue;
            }
            if (record.Counters is not ClaudeCounters counters)
            {
                findings.Add(SourceAuditSupport.Gap(record, "missing_counter"));
                continue;
            }
            if (record.MessageId is null)
            {
                findings.Add(SourceAuditSupport.Gap(record, "missing_message_identity"));
            }
            if (record.Model is null)
            {
                findings.Add(SourceAuditSupport.Gap(record, "missing_model"));
            }
            var closed = SourceAuditSupport.Closed(sessions[SourceAuditSupport.Session(record)], record);
            if (!closed)
            {
                findings.Add(SourceAuditSupport.Gap(record, "missing_closure"));
            }
            var tokens = Normalize(record, counters, findings);
            if (tokens is null)
            {
                continue;
            }
            groups.Add(
                new(
                    SourceAuditSupport.Key(record, "Anthropic", SourceAuditSupport.Subscription),
                    tokens,
                    record.Cost,
                    record.Currency,
                    !closed,
                    copies.Select(SourceAuditSupport.Reference).Order(StringComparer.Ordinal).ToArray()
                )
            );
        }
        return SourceAuditSupport.Result(groups, groups, findings, facts);
    }

    private static decimal Richness(SourceUsageRecord record)
    {
        if (record.Counters is not ClaudeCounters c)
        {
            return -1;
        }
        return (decimal)(c.InputTokens ?? 0)
            + (c.OutputTokens ?? 0)
            + (c.CacheCreationInputTokens ?? 0)
            + (c.CacheReadInputTokens ?? 0)
            + (c.ThinkingTokens ?? 0);
    }

    private static int Completeness(SourceUsageRecord record)
    {
        if (record.Counters is not ClaudeCounters c)
        {
            return 0;
        }
        long?[] fields =
        [
            c.InputTokens,
            c.OutputTokens,
            c.CacheCreationInputTokens,
            c.CacheReadInputTokens,
            c.CacheCreation5mInputTokens,
            c.CacheCreation1hInputTokens,
            c.ThinkingTokens,
            c.ThinkingOutputTokens,
        ];
        return fields.Count(v => v is not null);
    }

    private static UsageCounters? Normalize(
        SourceUsageRecord record,
        ClaudeCounters counters,
        List<AuditFinding> findings
    )
    {
        if (counters.InputTokens is null || counters.OutputTokens is null)
        {
            findings.Add(SourceAuditSupport.Gap(record, "missing_counter"));
            return null;
        }
        var creation = counters.CacheCreationInputTokens;
        if (
            creation is null
            && counters.CacheCreation5mInputTokens is { } five
            && counters.CacheCreation1hInputTokens is { } hour
        )
        {
            creation = AuditNumbers.AddCounters(five, hour);
        }
        if (creation is null || counters.CacheReadInputTokens is null)
        {
            findings.Add(SourceAuditSupport.Gap(record, "missing_counter"));
        }
        if (
            counters.CacheCreation1hInputTokens > creation
            || counters.CacheCreation5mInputTokens > creation
            || counters.CacheCreation5mInputTokens is { } shortTtl
                && counters.CacheCreation1hInputTokens is { } longTtl
                && creation is { } total
                && (decimal)shortTtl + longTtl != total
        )
        {
            findings.Add(SourceAuditSupport.Gap(record, "inconsistent_cache_subsets"));
        }
        if (counters.ThinkingOutputTokens is not null)
        {
            findings.Add(SourceAuditSupport.Gap(record, "unsupported_thinking_output_convention"));
        }
        return new(
            counters.InputTokens.Value,
            counters.OutputTokens.Value,
            counters.CacheReadInputTokens,
            creation,
            counters.CacheCreation1hInputTokens,
            counters.ThinkingTokens
        );
    }
}
