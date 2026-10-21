using System.Globalization;
using System.Text.Json;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;

namespace AiObservatory.Audit.Sources;

internal static class SourceAuditSupport
{
    internal static readonly ObservationProvenance Subscription = new(
        SourceKind.LocalTelemetry,
        UsageScope.Subscription,
        AccountingBasis.Notional
    );

    internal static SourceUsageRecord[] Prepare(
        IReadOnlyList<SourceUsageRecord> records,
        AuditScope scope,
        SourceTool tool
    )
    {
        _ = AuditCanonicalJson.Serialize(scope);
        var normalized = AuditCanonicalJson.Deserialize<SourceUsageRecord[]>(AuditCanonicalJson.Serialize(records));
        return normalized.Where(r => r.Tool == tool && scope.Sources.Contains(new(r.MachineId, r.SourceId))).ToArray();
    }

    internal static bool Supported(SourceUsageRecord record) =>
        AuditCapabilities.Sources.Any(c =>
            c.Tool == record.Tool && c.Supported && c.Format == record.SourceFormat && c.Version == record.SourceVersion
        );

    internal static DateOnly Date(SourceUsageRecord record) => DateOnly.FromDateTime(record.Timestamp.UtcDateTime);

    internal static bool InScope(DateOnly date, AuditScope scope) => date >= scope.From && date <= scope.To;

    internal static string Identity(params object?[] fields) =>
        AuditCanonicalJson.ContentHash(JsonSerializer.SerializeToUtf8Bytes(fields));

    internal static string Reference(SourceUsageRecord record) =>
        Identity(record.SourceId, record.Generation, record.FileId, record.Sequence);

    internal static string Session(SourceUsageRecord record) =>
        Identity(record.MachineId, record.SourceId, record.Generation, record.SessionId ?? record.FileId);

    internal static string Group(AccountingKey key) =>
        AuditCanonicalJson.ContentHash(AuditCanonicalJson.Serialize(key));

    internal static AccountingKey Key(SourceUsageRecord record, string provider, ObservationProvenance provenance) =>
        new(
            record.MachineId,
            record.SourceId,
            record.Generation,
            record.SessionId,
            Date(record),
            provider,
            record.Model,
            provenance,
            record.Pricing
        );

    internal static AuditFinding Gap(SourceUsageRecord record, string diagnostic) =>
        new(
            CheckCode.SourceAccounting,
            diagnostic,
            Session(record),
            [Reference(record)],
            "evidence",
            null,
            null,
            null,
            ValueUnit.Count,
            null,
            AccountingBasis.None,
            diagnostic
        );

    internal static bool Closed(IEnumerable<SourceUsageRecord> session, SourceUsageRecord last) =>
        session.Any(r =>
            r.Closure.State == ClosureState.ExplicitlyClosed
            && r.Closure.Timestamp >= last.Timestamp
            && r.Closure.Sequence >= last.Sequence
        );

    internal static SourceReconstruction Result(
        IEnumerable<ReconstructedGroup> compatible,
        IEnumerable<ReconstructedGroup> chronological,
        IReadOnlyList<AuditFinding> findings,
        SourceUsageRecord[] facts
    )
    {
        var compatibleArray = compatible.ToArray();
        var allFindings = findings.Concat(CurrencyFindings(compatibleArray, facts)).ToArray();
        var groups = Consolidate(compatibleArray);
        var observed = Consolidate(chronological);
        var sources = facts.GroupBy(Session).ToDictionary(g => g.Key, g => g.First().SourceId);
        var gaps = allFindings
            .Select(f => new CoverageGap(
                sources.GetValueOrDefault(f.GroupKey) ?? "unplaced",
                f.GroupKey,
                f.DiagnosticCode
            ))
            .Distinct()
            .ToArray();
        var total = groups.LongLength + gaps.LongLength;
        return new(
            groups,
            observed,
            new(groups.LongLength, total, gaps),
            allFindings
                .OrderBy(f => f.RuleCode, StringComparer.Ordinal)
                .ThenBy(f => f.GroupKey, StringComparer.Ordinal)
                .ThenBy(f => f.EvidenceRefs[0], StringComparer.Ordinal)
                .ToArray()
        );
    }

    private static IEnumerable<AuditFinding> CurrencyFindings(ReconstructedGroup[] groups, SourceUsageRecord[] facts)
    {
        foreach (var group in groups.GroupBy(g => g.Key).Where(g => g.Select(v => v.Currency).Distinct().Count() > 1))
        {
            var evidence = group.First().EvidenceRefs[0];
            var record = facts.First(r => Reference(r) == evidence);
            yield return Gap(record, "mixed_source_currencies");
        }
    }

    internal static ReconstructedGroup[] Consolidate(IEnumerable<ReconstructedGroup> groups) =>
        groups
            .GroupBy(g => g.Key)
            .Select(g => new ReconstructedGroup(
                g.Key,
                Sum(g.Select(x => x.Tokens)),
                CommonCurrency(g) is null ? null : SumMoney(g.Select(x => x.Cost)),
                CommonCurrency(g),
                g.Any(x => x.Provisional),
                g.SelectMany(x => x.EvidenceRefs).Distinct().Order(StringComparer.Ordinal).ToArray()
            ))
            .OrderBy(g => g.Key.Date)
            .ThenBy(g => Group(g.Key), StringComparer.Ordinal)
            .ToArray();

    private static decimal? SumMoney(IEnumerable<decimal?> values)
    {
        decimal total = 0;
        foreach (var value in values)
        {
            if (value is null)
            {
                return null;
            }
            try
            {
                total = checked(total + value.Value);
            }
            catch (OverflowException)
            {
                throw new AuditValidationException("money_overflow");
            }
        }
        return total;
    }

    private static string? CommonCurrency(IEnumerable<ReconstructedGroup> groups)
    {
        var currencies = groups.Select(g => g.Currency).Distinct().ToArray();
        return currencies.Length == 1 ? currencies[0] : null;
    }

    internal static UsageCounters Sum(IEnumerable<UsageCounters> counters)
    {
        var values = counters.ToArray();
        return new(
            Sum(values.Select(c => c.InputTokens)),
            Sum(values.Select(c => c.OutputTokens)),
            SumNullable(values.Select(c => c.CacheReadTokens)),
            SumNullable(values.Select(c => c.CacheWriteTokens)),
            SumNullable(values.Select(c => c.CacheWrite1hTokens)),
            SumNullable(values.Select(c => c.ThoughtTokens))
        );
    }

    private static long Sum(IEnumerable<long> values) => values.Aggregate(0L, AuditNumbers.AddCounters);

    private static long? SumNullable(IEnumerable<long?> values)
    {
        var array = values.ToArray();
        return array.Any(v => v is null) ? null : Sum(array.Select(v => v!.Value));
    }

    internal static AuditValue Integer(long value) =>
        new(AuditValueKind.Integer, value.ToString(CultureInfo.InvariantCulture));
}
