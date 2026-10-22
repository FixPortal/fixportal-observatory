using System.Globalization;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;

namespace AiObservatory.Audit.Checks;

internal static class ComparisonSupport
{
    internal static AuditInput Normalize(AuditInput input) =>
        AuditCanonicalJson.Deserialize<AuditInput>(AuditCanonicalJson.Serialize(input));

    internal static bool InScope(AuditScope scope, DateOnly date, string source) =>
        date >= scope.From && date <= scope.To && scope.Sources.Any(s => s.SourceId == source);

    internal static IReadOnlyList<FrozenEvent> Events(AuditInput input) =>
        input
            .Events.Where(e => InScope(input.Scope, DateOnly.FromDateTime(e.OccurredAt.UtcDateTime), e.SourceId))
            .ToArray();

    internal static AuditValue Integer(long value) =>
        new(AuditValueKind.Integer, value.ToString(CultureInfo.InvariantCulture));

    internal static AuditValue Money(decimal value) =>
        new(AuditValueKind.Decimal, value.ToString("0.############################", CultureInfo.InvariantCulture));

    internal static decimal Sum(IEnumerable<decimal> values)
    {
        try
        {
            return values.Sum();
        }
        catch (OverflowException)
        {
            throw new AuditValidationException("money_overflow");
        }
    }

    internal static decimal Difference(decimal expected, decimal actual) => Sum([actual, -expected]);

    internal static long CountSum(IEnumerable<long> values) => values.Aggregate(0L, AuditNumbers.AddCounters);

    internal static CheckOutcome Excluded(CheckCode code) => new(code, CheckVerdict.Excluded, new(0, 0, []), []);

    internal static CheckVerdict Verdict(int differences, int gaps)
    {
        if (differences > 0)
        {
            return CheckVerdict.Mismatched;
        }
        return gaps > 0 ? CheckVerdict.Incomplete : CheckVerdict.Matched;
    }

    internal static string Key(AggregateKey key) => AuditCanonicalJson.ContentHash(AuditCanonicalJson.Serialize(key));

    internal static IReadOnlyList<CoverageExclusion> MachineExclusion { get; } =
    [new("machineAttribution", "production_rows_have_no_machine_identity")];

    internal static AuditFinding Finding(
        CheckCode code,
        string rule,
        string key,
        IReadOnlyList<string> refs,
        string field,
        AuditValue? expected,
        AuditValue? actual,
        AuditValue? delta,
        ValueUnit unit,
        string? currency,
        AccountingBasis basis
    ) => new(code, rule, key, refs, field, expected, actual, delta, unit, currency, basis, rule);
}
