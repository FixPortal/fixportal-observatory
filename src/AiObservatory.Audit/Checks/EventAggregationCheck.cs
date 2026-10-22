using AiObservatory.Audit.Contracts;

namespace AiObservatory.Audit.Checks;

/// <summary>Reconstructs the reported aggregate fields from frozen current event rows without repository logic.</summary>
public sealed class EventAggregationCheck
{
    private const CheckCode Code = CheckCode.EventAggregation;

    public CheckOutcome Evaluate(AuditInput input)
    {
        input = ComparisonSupport.Normalize(input);
        if (input.Scope.ExcludedChecks.Contains(Code))
        {
            return ComparisonSupport.Excluded(Code);
        }
        var groups = ComparisonSupport.Events(input).GroupBy(Key).ToDictionary(g => g.Key, g => g.ToArray());
        var actual = input
            .Aggregates.Where(a => ComparisonSupport.InScope(input.Scope, a.Key.Date, a.Key.SourceId))
            .ToDictionary(a => a.Key);
        var keys = groups.Keys.Union(actual.Keys).OrderBy(ComparisonSupport.Key, StringComparer.Ordinal).ToArray();
        var findings = new List<AuditFinding>();
        var compared = 0L;
        foreach (var key in keys)
        {
            var events = groups.GetValueOrDefault(key) ?? [];
            var stored = actual.GetValueOrDefault(key);
            Compare(input, key, events, stored, findings);
            if (events.Length > 0 && stored is not null)
            {
                compared++;
            }
        }
        CoverageGap[] gaps = keys.Length == 0 ? [new("all", "all", "no_aggregate_evidence")] : [];
        return new(
            Code,
            ComparisonSupport.Verdict(findings.Count, gaps.Length),
            new(compared, keys.LongLength + gaps.Length, gaps)
            {
                Exclusions =
                [
                    new("thoughtTokens", "aggregate_has_no_thought_tokens_column"),
                    .. ComparisonSupport.MachineExclusion,
                ],
            },
            findings
        );
    }

    private static AggregateKey Key(FrozenEvent e) =>
        new(
            DateOnly.FromDateTime(e.OccurredAt.UtcDateTime),
            e.Provider,
            e.Model ?? "",
            e.SourceId,
            e.Provenance.SourceKind,
            e.Provenance.UsageScope,
            e.Provenance.CostBasis
        );

    private static void Compare(
        AuditInput input,
        AggregateKey key,
        FrozenEvent[] events,
        FrozenAggregate? stored,
        List<AuditFinding> findings
    )
    {
        var group = ComparisonSupport.Key(key);
        var refs = events.Select(e => e.RowId.ToString("D")).ToArray();
        (string Field, long Expected, long Actual)[] counters =
        [
            ("inputTokens", Sum(events, e => e.Tokens.InputTokens), stored?.InputTokens ?? 0),
            ("outputTokens", Sum(events, e => e.Tokens.OutputTokens), stored?.OutputTokens ?? 0),
            ("cacheReadTokens", Sum(events, e => e.Tokens.CacheReadTokens ?? 0), stored?.CacheReadTokens ?? 0),
            ("cacheWriteTokens", Sum(events, e => e.Tokens.CacheWriteTokens ?? 0), stored?.CacheWriteTokens ?? 0),
            ("cacheWrite1hTokens", Sum(events, e => e.Tokens.CacheWrite1hTokens ?? 0), stored?.CacheWrite1hTokens ?? 0),
            (
                "unknownCostCount",
                events.LongCount(e => e.CostUsd is null && e.Provenance.CostBasis != AccountingBasis.None),
                stored?.UnknownCostCount ?? 0
            ),
            (
                "unknownCacheSavingsCount",
                events.LongCount(e => e.CacheSavingsUsd is null && e.Provenance.CostBasis != AccountingBasis.None),
                stored?.UnknownCacheSavingsCount ?? 0
            ),
            ("requestCount", events.LongLength, stored?.RequestCount ?? 0),
        ];
        foreach (var (field, expected, actual) in counters.Where(c => c.Expected != c.Actual))
        {
            var unit = field.EndsWith("Tokens", StringComparison.Ordinal) ? ValueUnit.Tokens : ValueUnit.Count;
            findings.Add(
                ComparisonSupport.Finding(
                    Code,
                    "event_aggregation_difference",
                    group,
                    refs,
                    field,
                    ComparisonSupport.Integer(expected),
                    ComparisonSupport.Integer(actual),
                    ComparisonSupport.Integer(actual - expected),
                    unit,
                    null,
                    key.CostBasis
                )
            );
        }
        CompareMoney(
            input,
            key,
            group,
            refs,
            "costUsd",
            ComparisonSupport.Sum(events.Select(e => e.CostUsd ?? 0)),
            stored?.CostUsd ?? 0,
            findings
        );
        CompareMoney(
            input,
            key,
            group,
            refs,
            "cacheSavingsUsd",
            ComparisonSupport.Sum(events.Select(e => e.CacheSavingsUsd ?? 0)),
            stored?.CacheSavingsUsd ?? 0,
            findings
        );
        if (events.Length == 0 || stored is null)
        {
            var expected = events.Length == 0 ? 0L : 1L;
            var actual = stored is null ? 0L : 1L;
            findings.Add(
                ComparisonSupport.Finding(
                    Code,
                    "event_aggregation_difference",
                    group,
                    refs,
                    "groupPresence",
                    ComparisonSupport.Integer(expected),
                    ComparisonSupport.Integer(actual),
                    ComparisonSupport.Integer(actual - expected),
                    ValueUnit.Count,
                    null,
                    key.CostBasis
                )
            );
        }
    }

    private static long Sum(IEnumerable<FrozenEvent> events, Func<FrozenEvent, long> select) =>
        ComparisonSupport.CountSum(events.Select(select));

    private static void CompareMoney(
        AuditInput input,
        AggregateKey key,
        string group,
        string[] refs,
        string field,
        decimal expected,
        decimal actual,
        List<AuditFinding> findings
    )
    {
        var tolerance = input.Tolerances.SingleOrDefault(t => t.Name == "eventAggregation." + field);
        if (tolerance is not null && (tolerance.Unit != ValueUnit.Money || tolerance.Currency != "USD"))
        {
            throw new AuditValidationException("invalid_comparison_tolerance");
        }
        var delta = ComparisonSupport.Difference(expected, actual);
        if (Math.Abs(delta) > (tolerance?.AbsoluteTolerance ?? 0))
        {
            findings.Add(
                ComparisonSupport.Finding(
                    Code,
                    "event_aggregation_difference",
                    group,
                    refs,
                    field,
                    ComparisonSupport.Money(expected),
                    ComparisonSupport.Money(actual),
                    ComparisonSupport.Money(delta),
                    ValueUnit.Money,
                    "USD",
                    key.CostBasis
                )
            );
        }
    }
}
