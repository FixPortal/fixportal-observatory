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
            AddMessage(copies.ToArray(), sessions, scope, findings, groups);
        }
        return SourceAuditSupport.Result(groups, groups, findings, facts);
    }

    private static void AddMessage(
        SourceUsageRecord[] copies,
        Dictionary<string, SourceUsageRecord[]> sessions,
        AuditScope scope,
        List<AuditFinding> findings,
        List<ReconstructedGroup> groups
    )
    {
        if (copies.Select(r => r.SourceVersion).Distinct(StringComparer.Ordinal).Skip(1).Any())
        {
            // Prepare validates every timestamp. Only wholly out-of-period copies
            // can be ignored; disagreement must never select an authoritative version.
            if (copies.All(r => !SourceAuditSupport.InScope(SourceAuditSupport.Date(r), scope)))
            {
                return;
            }
            findings.Add(SourceAuditSupport.Gap(copies[0], "mixed_source_versions"));
            return;
        }
        var record = copies
            .OrderByDescending(r => r.SourceVersion == "2" ? ProspectiveRichness(r) : Richness(r))
            .ThenByDescending(Completeness)
            .ThenBy(r => r.Sequence)
            .ThenBy(r => r.FileId, StringComparer.Ordinal)
            .First();
        if (!SourceAuditSupport.InScope(SourceAuditSupport.Date(record), scope))
        {
            return;
        }
        if (!SourceAuditSupport.Supported(record))
        {
            findings.Add(SourceAuditSupport.Gap(record, "unsupported_source_format"));
            return;
        }
        if (record.Counters is not ClaudeCounters counters)
        {
            findings.Add(SourceAuditSupport.Gap(record, "missing_counter"));
            return;
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
        var tokens = NormalizeVersioned(record, counters, findings);
        if (tokens is null)
        {
            return;
        }
        if (record.SourceVersion == "2")
        {
            var context = PromptContext(copies.ToArray());
            if (MissingPromptContext(copies, context))
            {
                findings.Add(SourceAuditSupport.Gap(record, "claude_prompt_context_unknown"));
            }
            // A companion-supplied derived context is never proof. Capacity stays unchanged.
            record = record with
            {
                Pricing = record.Pricing with { Context = context },
            };
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

    private static bool MissingPromptContext(SourceUsageRecord[] copies, string? context) =>
        context is null && copies.Any(r => TieredModel(r.Model));

    private static UsageCounters? NormalizeVersioned(
        SourceUsageRecord record,
        ClaudeCounters counters,
        List<AuditFinding> findings
    )
    {
        try
        {
            return Normalize(record, counters, findings);
        }
        catch (AuditValidationException error) when (record.SourceVersion == "2" && error.Code == "counter_overflow")
        {
            findings.Add(SourceAuditSupport.Gap(record, "claude_prompt_context_unknown"));
            return null;
        }
    }

    private static decimal ProspectiveRichness(SourceUsageRecord record) =>
        record.Counters is ClaudeCounters c
            ? (decimal)(c.InputTokens ?? 0)
                + (c.OutputTokens ?? 0)
                + (
                    c.CacheCreationInputTokens
                    ?? ((decimal)(c.CacheCreation5mInputTokens ?? 0) + (c.CacheCreation1hInputTokens ?? 0))
                )
                + (c.CacheReadInputTokens ?? 0)
                + (c.ThinkingTokens ?? 0)
            : -1;

    // Rule 2 qualifies only these identities. This policy is intentionally independent
    // of the production parser, catalog and calculator, and is bounded by fixtures.
    private static bool TieredModel(string? model)
    {
        const string canonical = "claude-haiku-5-5";
        if (string.Equals(model, canonical, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return model is not null
            && model.Length == canonical.Length + 9
            && model.StartsWith(canonical + "-", StringComparison.OrdinalIgnoreCase)
            && DateOnly.TryParseExact(
                model[(canonical.Length + 1)..],
                "yyyyMMdd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out _
            );
    }

    private static string? PromptContext(SourceUsageRecord[] copies)
    {
        if (copies.Any(r => r.MessageId is null || r.Counters is not ClaudeCounters))
        {
            return null;
        }
        var candidate = copies.FirstOrDefault(r =>
            TieredModel(r.Model) && PromptSize((ClaudeCounters)r.Counters!) is not null
        );
        if (candidate is null)
        {
            return null;
        }
        var facts = new Dictionary<string, long>(StringComparer.Ordinal);
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (
            copies.Any(copy =>
                !Agree(CounterFacts((ClaudeCounters)copy.Counters!), facts) || !Agree(Metadata(copy), metadata)
            )
        )
        {
            return null;
        }
        var c = (ClaudeCounters)candidate.Counters!;
        var creation = CompleteCreation(c);
        // Reject contradictory partial subsets across copies, without using their union
        // to manufacture a complete candidate.
        if (!ValidCreation(creation, facts.GetValueOrDefault("five", -1), facts.GetValueOrDefault("hour", -1)))
        {
            return null;
        }
        if (facts.TryGetValue("creation", out var observedCreation) && observedCreation != creation)
        {
            return null;
        }
        return PromptSize(c) <= 100000 ? "prompt_le_100000" : "prompt_gt_100000";
    }

    private static bool Agree<T>(IEnumerable<KeyValuePair<string, T>> observations, Dictionary<string, T> agreed)
    {
        foreach (var (key, value) in observations)
        {
            if (agreed.TryGetValue(key, out var previous) && !EqualityComparer<T>.Default.Equals(previous, value))
            {
                return false;
            }
            agreed[key] = value;
        }
        return true;
    }

    private static IEnumerable<KeyValuePair<string, long>> CounterFacts(ClaudeCounters c)
    {
        (string Key, long? Value)[] fields =
        [
            ("input", c.InputTokens),
            ("read", c.CacheReadInputTokens),
            ("creation", CompleteCreation(c)),
            ("five", c.CacheCreation5mInputTokens),
            ("hour", c.CacheCreation1hInputTokens),
        ];
        return fields
            .Where(f => f.Value is not null)
            .Select(f => new KeyValuePair<string, long>(f.Key, f.Value!.Value));
    }

    private static IEnumerable<KeyValuePair<string, string>> Metadata(SourceUsageRecord r)
    {
        (string Key, string? Value)[] fields =
        [
            ("model", r.Model),
            ("tier", r.Pricing.SourceServiceTier),
            ("speed", r.Pricing.Speed),
            ("geo", r.Pricing.InferenceGeo),
            ("region", r.Pricing.Region),
        ];
        return fields
            .Where(f => f.Value is not null)
            .Select(f => new KeyValuePair<string, string>(f.Key, f.Value!.ToLowerInvariant()));
    }

    private static long? CompleteCreation(ClaudeCounters c)
    {
        try
        {
            return c.CacheCreationInputTokens
                ?? (
                    c.CacheCreation5mInputTokens is { } five && c.CacheCreation1hInputTokens is { } hour
                        ? checked(five + hour)
                        : null
                );
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static long? PromptSize(ClaudeCounters c)
    {
        var creation = CompleteCreation(c);
        if (
            c.InputTokens is not { } input
            || c.CacheReadInputTokens is not { } read
            || !ValidCreation(creation, c.CacheCreation5mInputTokens ?? -1, c.CacheCreation1hInputTokens ?? -1)
        )
        {
            return null;
        }
        try
        {
            return checked(input + read + creation!.Value);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static bool ValidCreation(long? total, long five, long hour) =>
        total is { } value && five <= value && hour <= value && (five < 0 || hour < 0 || (decimal)five + hour == value);

    private static decimal Richness(SourceUsageRecord record)
    {
        if (record.Counters is not ClaudeCounters c)
        {
            return -1;
        }
        return (decimal)(c.InputTokens ?? 0)
            + (c.OutputTokens ?? 0)
            + (Creation(c) ?? 0)
            + (c.CacheReadInputTokens ?? 0)
            + (c.ThinkingTokens ?? 0);
    }

    private static long? Creation(ClaudeCounters counters)
    {
        if (counters.CacheCreationInputTokens is { } aggregate)
        {
            return aggregate;
        }
        return counters.CacheCreation5mInputTokens is { } five && counters.CacheCreation1hInputTokens is { } hour
            ? AuditNumbers.AddCounters(five, hour)
            : null;
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
        var creation = Creation(counters);
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
