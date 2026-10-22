using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using AiObservatory.Audit.Sources;

namespace AiObservatory.Audit.Checks;

internal sealed class SourceCheckEvaluation
{
    private readonly ISourceAuditAdapter[] adapters = [new ClaudeAuditAdapter(), new CodexAuditAdapter()];

    internal CheckOutcome Evaluate(AuditInput input, CheckCode code)
    {
        if (input.Scope.ExcludedChecks.Contains(code))
        {
            return new(code, CheckVerdict.Excluded, new(0, 0, []), []);
        }
        input = AuditCanonicalJson.Deserialize<AuditInput>(AuditCanonicalJson.Serialize(input));
        var findings = new List<AuditFinding>();
        var gaps = new List<CoverageGap>();
        var reconstructions = adapters.Select(adapter => adapter.Reconstruct(input.Records, input.Scope)).ToArray();
        findings.AddRange(reconstructions.SelectMany(r => r.Findings).Select(f => f with { CheckCode = code }));
        gaps.AddRange(reconstructions.SelectMany(r => r.Coverage.Gaps));
        var compatible = reconstructions.SelectMany(r => r.CompatibleGroups).ToArray();
        var expected =
            code == CheckCode.DailyAttribution
                ? reconstructions.SelectMany(r => r.ChronologicalGroups).ToArray()
                : compatible;
        var unsupported = Coverage(input, code, expected, findings, gaps);
        var expectedMap = Groups(expected);
        var actualMap = code == CheckCode.DailyAttribution ? Groups(compatible) : Events(input, code, findings, gaps);
        var keys = expectedMap.Keys.Union(actualMap.Keys).OrderBy(k => k.Id, StringComparer.Ordinal).ToArray();
        var compared = 0L;
        foreach (var key in keys)
        {
            var reference = expectedMap.GetValueOrDefault(key);
            var actual = actualMap.GetValueOrDefault(key);
            Compare(key, reference, actual, code, findings, gaps);
            if (reference is not null && actual is not null)
            {
                compared++;
            }
        }
        var hasDifferences =
            findings.Any(f =>
                f.RuleCode is "source_accounting_difference" or "daily_attribution_difference"
                && f.Field != "groupPresence"
                && f.Expected is not null
                && f.Actual is not null
            )
            || keys.Any(key =>
                expectedMap.ContainsKey(key) != actualMap.ContainsKey(key) && !gaps.Any(g => g.SourceId == key.SourceId)
            );
        var verdict = ComparisonSupport.Verdict(hasDifferences ? 1 : 0, gaps.Count);
        if (
            !hasDifferences
            && unsupported
            && input.Records.All(r => !AuditCapabilities.Sources.Any(c => c.Tool == r.Tool && c.Supported))
        )
        {
            verdict = CheckVerdict.Unsupported;
        }
        return new(
            code,
            verdict,
            new(
                compared,
                keys.LongLength + gaps.Count,
                gaps.Distinct()
                    .OrderBy(g => g.SourceId, StringComparer.Ordinal)
                    .ThenBy(g => g.GroupKey, StringComparer.Ordinal)
                    .ThenBy(g => g.DiagnosticCode, StringComparer.Ordinal)
                    .ToArray()
            ),
            findings
                .OrderBy(f => f.RuleCode, StringComparer.Ordinal)
                .ThenBy(f => f.GroupKey, StringComparer.Ordinal)
                .ThenBy(f => f.Field, StringComparer.Ordinal)
                .ToArray()
        );
    }

    private static bool Coverage(
        AuditInput input,
        CheckCode code,
        ReconstructedGroup[] expected,
        List<AuditFinding> findings,
        List<CoverageGap> gaps
    )
    {
        var unsupported = false;
        foreach (var selection in input.Scope.Sources)
        {
            var records = input
                .Records.Where(r => r.MachineId == selection.MachineId && r.SourceId == selection.SourceId)
                .ToArray();
            foreach (var tool in records.Select(r => r.Tool).Distinct())
            {
                var capability = AuditCapabilities.Sources.Single(c => c.Tool == tool);
                unsupported |= !capability.Supported;
                if (capability.ReasonCode is { } reason)
                {
                    AddGap(selection.SourceId, reason, code, findings, gaps);
                }
            }
            SelectionCoverage(input, selection, records, expected, code, findings, gaps);
        }
        return unsupported;
    }

    private static void SelectionCoverage(
        AuditInput input,
        SourceSelection selection,
        SourceUsageRecord[] records,
        ReconstructedGroup[] expected,
        CheckCode code,
        List<AuditFinding> findings,
        List<CoverageGap> gaps
    )
    {
        if (records.Length == 0)
        {
            AddGap(
                selection.SourceId,
                "no_usage_records",
                code,
                findings,
                gaps,
                SourceAuditSupport.Identity(selection.MachineId, selection.SourceId)
            );
            return;
        }
        foreach (var generation in records.GroupBy(r => r.Generation))
        {
            var groupKey = SourceAuditSupport.Identity(selection.MachineId, selection.SourceId, generation.Key);
            if (
                !expected.Any(g =>
                    g.Key.MachineId == selection.MachineId
                    && g.Key.SourceId == selection.SourceId
                    && g.Key.Generation == generation.Key
                )
            )
            {
                AddGap(selection.SourceId, "no_usage_records", code, findings, gaps, groupKey);
            }
            if (!HasCapture(input, selection, generation))
            {
                AddGap(selection.SourceId, "incomplete_capture", code, findings, gaps, groupKey);
            }
        }
    }

    private static bool CompleteScan(CaptureManifest capture) =>
        capture.ReadFiles == capture.DiscoveredFiles
        && capture.SkippedFiles == 0
        && capture.ChangedFiles == 0
        && capture.Diagnostics.All(d => d.Count == 0)
        && capture.SealedAt >= capture.ScanEndedAt;

    private static bool HasCapture(
        AuditInput input,
        SourceSelection selection,
        IGrouping<string, SourceUsageRecord> generation
    ) =>
        input.Captures.Any(c =>
            c.MachineId == selection.MachineId
            && c.Generation == generation.Key
            && c.Scope.Sources.Contains(selection)
            && c.Scope.From <= input.Scope.From
            && c.Scope.To >= input.Scope.To
            && generation.All(r =>
                SourceAuditSupport.Supported(r)
                && c.SourceFormats.Any(f =>
                    f.SourceId == selection.SourceId && f.Format == r.SourceFormat && f.Version == r.SourceVersion
                )
            )
            && CompleteScan(c)
        );

    private static void AddGap(
        string source,
        string diagnostic,
        CheckCode code,
        List<AuditFinding> findings,
        List<CoverageGap> gaps,
        string? groupKey = null
    )
    {
        gaps.Add(new(source, groupKey ?? source, diagnostic));
        findings.Add(
            new(
                code,
                diagnostic,
                groupKey ?? source,
                [],
                "evidence",
                null,
                null,
                null,
                ValueUnit.Count,
                null,
                AccountingBasis.None,
                diagnostic
            )
        );
    }

    private static Dictionary<ComparisonKey, ComparisonGroup> Groups(IEnumerable<ReconstructedGroup> groups) =>
        groups
            .GroupBy(g => ComparisonKey.From(g.Key))
            .ToDictionary(
                g => g.Key,
                g => new ComparisonGroup(
                    SourceAuditSupport.Sum(g.Select(v => v.Tokens)),
                    g.SelectMany(v => v.EvidenceRefs).Distinct().Order(StringComparer.Ordinal).ToArray()
                )
            );

    private static Dictionary<ComparisonKey, ComparisonGroup> Events(
        AuditInput input,
        CheckCode code,
        List<AuditFinding> findings,
        List<CoverageGap> gaps
    )
    {
        var events = input
            .Events.Where(e =>
                input.Scope.Sources.Any(s => s.SourceId == e.SourceId)
                && SourceAuditSupport.InScope(DateOnly.FromDateTime(e.OccurredAt.UtcDateTime), input.Scope)
            )
            .ToArray();
        foreach (var source in events.Where(e => e.PricingApplication is null).Select(e => e.SourceId).Distinct())
        {
            AddGap(source, "missing_pricing_dimensions", code, findings, gaps);
        }
        return events
            .Where(e => e.PricingApplication is not null)
            .GroupBy(e => new ComparisonKey(
                e.SourceId,
                DateOnly.FromDateTime(e.OccurredAt.UtcDateTime),
                e.Provider,
                e.Model,
                e.Provenance,
                e.PricingApplication!.Dimensions
            ))
            .ToDictionary(
                g => g.Key,
                g => new ComparisonGroup(
                    SourceAuditSupport.Sum(g.Select(e => e.Tokens)),
                    g.Select(e => e.RowId.ToString()).Order(StringComparer.Ordinal).ToArray()
                )
            );
    }

    private static void Compare(
        ComparisonKey key,
        ComparisonGroup? reference,
        ComparisonGroup? actual,
        CheckCode code,
        List<AuditFinding> findings,
        List<CoverageGap> gaps
    )
    {
        var rule = code == CheckCode.DailyAttribution ? "daily_attribution_difference" : "source_accounting_difference";
        if (reference is null || actual is null)
        {
            findings.Add(
                new(
                    code,
                    rule,
                    key.Id,
                    reference?.Evidence ?? actual!.Evidence,
                    "groupPresence",
                    SourceAuditSupport.Integer(reference is null ? 0 : 1),
                    SourceAuditSupport.Integer(actual is null ? 0 : 1),
                    SourceAuditSupport.Integer(reference is null ? 1 : -1),
                    ValueUnit.Count,
                    null,
                    key.Provenance.CostBasis,
                    rule
                )
            );
            return;
        }
        (string Field, long? Expected, long? Actual)[] values =
        [
            ("inputTokens", reference.Tokens.InputTokens, actual.Tokens.InputTokens),
            ("outputTokens", reference.Tokens.OutputTokens, actual.Tokens.OutputTokens),
            ("cacheReadTokens", reference.Tokens.CacheReadTokens, actual.Tokens.CacheReadTokens),
            ("cacheWriteTokens", reference.Tokens.CacheWriteTokens, actual.Tokens.CacheWriteTokens),
            ("cacheWrite1hTokens", reference.Tokens.CacheWrite1hTokens, actual.Tokens.CacheWrite1hTokens),
            ("thoughtTokens", reference.Tokens.ThoughtTokens, actual.Tokens.ThoughtTokens),
        ];
        foreach (var (field, expected, observed) in values.Where(v => v.Expected != v.Actual))
        {
            if (expected is null || observed is null)
            {
                gaps.Add(new(key.SourceId, key.Id + "|" + field, "unknown_token_comparison"));
            }
            findings.Add(
                new(
                    code,
                    rule,
                    key.Id,
                    reference.Evidence,
                    field,
                    expected is { } e ? SourceAuditSupport.Integer(e) : null,
                    observed is { } a ? SourceAuditSupport.Integer(a) : null,
                    expected is { } ev && observed is { } av ? SourceAuditSupport.Integer(av - ev) : null,
                    ValueUnit.Tokens,
                    null,
                    key.Provenance.CostBasis,
                    rule
                )
            );
        }
    }

    private sealed record ComparisonGroup(UsageCounters Tokens, IReadOnlyList<string> Evidence);

    private sealed record ComparisonKey(
        string SourceId,
        DateOnly Date,
        string Provider,
        string? Model,
        ObservationProvenance Provenance,
        PricingDimensions Pricing
    )
    {
        internal string Id => SourceAuditSupport.Identity(SourceId, Date, Provider, Model, Provenance, Pricing);

        internal static ComparisonKey From(AccountingKey key) =>
            new(key.SourceId, key.Date, key.Provider, key.Model, key.Provenance, key.Pricing);
    }
}
