using System.Globalization;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;

namespace AiObservatory.Audit.Checks;

/// <summary>Independent summary of frozen postings. A frontend probe supplies the actual side only.</summary>
public sealed class PresentationTotalsCheck
{
    private const CheckCode Code = CheckCode.PresentationTotals;

    /// <summary>Binds a probe to all frozen evidence and semantic options, excluding the probe and run linkage.</summary>
    public static string SummaryInputDigest(AuditInput input) =>
        AuditComputationInput.ContentDigest(input with { PresentationProbe = null });

    public static IReadOnlyList<PresentationSummaryValue> Reference(AuditInput input) =>
        Calculate(ComparisonSupport.Normalize(input));

    public CheckOutcome Evaluate(AuditInput input)
    {
        input = ComparisonSupport.Normalize(input);
        if (input.Scope.ExcludedChecks.Contains(Code))
        {
            return ComparisonSupport.Excluded(Code);
        }
        var reference = Calculate(input);
        var gaps = Qualifiers(reference);
        var findings = new List<AuditFinding>();
        var compared = 0L;
        if (input.PresentationProbe is not { } probe)
        {
            gaps.Add(new("all", "all", "missing_presentation_probe"));
        }
        else if (probe.SummaryInputDigest != SummaryInputDigest(input))
        {
            gaps.Add(new("all", "all", "stale_presentation_probe"));
        }
        else
        {
            compared = Compare(reference, probe.Values, gaps, findings);
        }
        return new(
            Code,
            ComparisonSupport.Verdict(findings.Count, gaps.Count),
            new(compared, reference.Count + gaps.Count, gaps) { Exclusions = ComparisonSupport.MachineExclusion },
            findings
        );
    }

    private static IReadOnlyList<PresentationSummaryValue> Calculate(AuditInput input)
    {
        var events = ComparisonSupport.Events(input);
        var spend = input
            .Spend.Where(s =>
                ComparisonSupport.InScope(input.Scope, s.OccurredOn, s.SourceId)
                && s.Provenance.CostBasis == AccountingBasis.Billed
            )
            .ToArray();
        var values = new List<PresentationSummaryValue>();
        foreach (var currency in spend.GroupBy(s => s.Currency).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            values.Add(
                Value("billedNative", ComparisonSupport.Sum(currency.Select(s => s.Amount)), currency.Key, currency.Key)
            );
        }
        values.Add(
            Value("billedGbp", spend.Length == 0 ? null : ComparisonSupport.Sum(spend.Select(s => s.AmountGbp)), "GBP")
        );
        AddBasis(
            values,
            events,
            AccountingBasis.ListPriceEstimate,
            "listPriceEstimateUsd",
            "unknownListPriceObservations"
        );
        AddBasis(
            values,
            events,
            AccountingBasis.ProviderEstimated,
            "providerEstimateUsd",
            "unknownProviderEstimateObservations"
        );
        AddBasis(values, events, AccountingBasis.Notional, "notionalUsd", "unknownNotionalObservations");
        var unclassified = events
            .Where(e =>
                e.Provenance.CostBasis is AccountingBasis.Unknown or AccountingBasis.Billed
                && e.CostUsd is not null and not 0
            )
            .ToArray();
        values.Add(
            Value(
                "unclassifiedUsd",
                unclassified.Length == 0 ? null : ComparisonSupport.Sum(unclassified.Select(e => e.CostUsd!.Value)),
                "USD"
            )
        );
        values.Add(Value("unknownCostObservations", events.LongCount(UnknownCost), "XXX"));
        AddSavings(values, events);
        return values
            .OrderBy(v => v.Field, StringComparer.Ordinal)
            .ThenBy(v => v.GroupKey, StringComparer.Ordinal)
            .ToArray();
    }

    private static void AddBasis(
        List<PresentationSummaryValue> values,
        IReadOnlyList<FrozenEvent> events,
        AccountingBasis basis,
        string field,
        string unknownField
    )
    {
        var bucket = events.Where(e => e.Provenance.CostBasis == basis).ToArray();
        var known = bucket.Where(e => e.CostUsd.HasValue).ToArray();
        values.Add(
            Value(field, known.Length == 0 ? null : ComparisonSupport.Sum(known.Select(e => e.CostUsd!.Value)), "USD")
        );
        values.Add(Value(unknownField, bucket.LongCount(UnknownCost), "XXX"));
    }

    private static bool UnknownCost(FrozenEvent e) =>
        e.CostUsd is null && e.Provenance.CostBasis != AccountingBasis.None;

    private static bool UnknownSavings(FrozenEvent e) =>
        e.CacheSavingsUsd is null && e.Provenance.CostBasis != AccountingBasis.None;

    private static void AddSavings(List<PresentationSummaryValue> values, IReadOnlyList<FrozenEvent> events)
    {
        // Cache eligibility belongs to the complete displayed aggregate bucket. Rows with no cache
        // traffic still contribute if other postings in that same bucket have observed cache traffic.
        var groups = events.GroupBy(e => new AggregateKey(
            DateOnly.FromDateTime(e.OccurredAt.UtcDateTime),
            e.Provider,
            e.Model ?? "unknown",
            e.SourceId,
            e.Provenance.SourceKind,
            e.Provenance.UsageScope,
            e.Provenance.CostBasis
        ));
        var eligible = groups
            .Where(g => g.Any(e => e.Tokens.CacheReadTokens is > 0 || e.Tokens.CacheWriteTokens is > 0))
            .ToArray();
        var known = eligible.Where(g => g.Any(e => !UnknownSavings(e))).SelectMany(g => g).ToArray();
        values.Add(
            Value(
                "cacheSavingsUsd",
                known.Length == 0 ? null : ComparisonSupport.Sum(known.Select(e => e.CacheSavingsUsd ?? 0)),
                "USD"
            )
        );
        values.Add(
            Value(
                "unknownCacheSavingsObservations",
                ComparisonSupport.CountSum(eligible.Select(g => g.LongCount(UnknownSavings))),
                "XXX"
            )
        );
    }

    private static PresentationSummaryValue Value(string field, decimal? value, string currency, string group = "all")
    {
        string? formatted = null;
        if (value is { } amount)
        {
            formatted =
                currency == "XXX"
                    ? amount.ToString("0", CultureInfo.InvariantCulture)
                    : decimal.Round(amount, 2, MidpointRounding.AwayFromZero)
                        .ToString("F2", CultureInfo.InvariantCulture);
        }
        return new(field, group, value, formatted, currency);
    }

    private static List<CoverageGap> Qualifiers(IReadOnlyList<PresentationSummaryValue> values)
    {
        var gaps = new List<CoverageGap>();
        (string Field, string Diagnostic)[] fields =
        [
            ("unknownCostObservations", "unknown_cost"),
            ("unknownCacheSavingsObservations", "unknown_cache_savings"),
            ("unknownListPriceObservations", "unknown_list_price_cost"),
            ("unknownProviderEstimateObservations", "unknown_provider_estimate_cost"),
            ("unknownNotionalObservations", "unknown_notional_cost"),
            ("unclassifiedUsd", "unclassified_cost"),
        ];
        foreach (var (field, diagnostic) in fields)
        {
            if (values.Any(v => v.Field == field && v.UnroundedValue is not null and not 0))
            {
                gaps.Add(new("all", field, diagnostic));
            }
        }
        return gaps;
    }

    private static long Compare(
        IReadOnlyList<PresentationSummaryValue> reference,
        IReadOnlyList<PresentationSummaryValue> actual,
        List<CoverageGap> gaps,
        List<AuditFinding> findings
    )
    {
        var compared = 0L;
        foreach (var expected in reference)
        {
            var observed = actual.SingleOrDefault(v => v.Field == expected.Field && v.GroupKey == expected.GroupKey);
            if (observed is null)
            {
                gaps.Add(new("all", expected.Field + "|" + expected.GroupKey, "missing_presentation_value"));
                continue;
            }
            compared++;
            CompareValue(expected, observed, findings);
        }
        foreach (var extra in actual.Where(a => !reference.Any(e => e.Field == a.Field && e.GroupKey == a.GroupKey)))
        {
            findings.Add(Finding("unexpected_presentation_value", extra, null, extra.UnroundedValue, null));
        }
        return compared;
    }

    private static void CompareValue(
        PresentationSummaryValue expected,
        PresentationSummaryValue actual,
        List<AuditFinding> findings
    )
    {
        var count = expected.Currency == "XXX";
        var expectedRounded = Comparable(expected.UnroundedValue, count);
        var actualRounded = Comparable(actual.UnroundedValue, count);
        var delta =
            expected.UnroundedValue.HasValue && actual.UnroundedValue.HasValue
                ? ComparisonSupport.Difference(expected.UnroundedValue.Value, actual.UnroundedValue.Value)
                : (decimal?)null;
        if (expectedRounded != actualRounded || expected.Currency != actual.Currency)
        {
            findings.Add(
                Finding(
                    "presentation_totals_difference",
                    expected,
                    expected.UnroundedValue,
                    actual.UnroundedValue,
                    delta
                )
            );
        }
        if (expected.FormattedValue != actual.FormattedValue)
        {
            var expectedFormat = Parse(expected.FormattedValue);
            var actualFormat = Parse(actual.FormattedValue);
            findings.Add(
                Finding(
                    "presentation_format_difference",
                    expected,
                    expectedFormat,
                    actualFormat,
                    expectedFormat.HasValue && actualFormat.HasValue
                        ? ComparisonSupport.Difference(expectedFormat.Value, actualFormat.Value)
                        : null
                )
            );
        }
    }

    private static decimal? Comparable(decimal? value, bool count) =>
        value is { } amount && !count ? decimal.Round(amount, 2, MidpointRounding.AwayFromZero) : value;

    private static decimal? Parse(string? formatted) =>
        decimal.TryParse(formatted, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static AuditFinding Finding(
        string rule,
        PresentationSummaryValue value,
        decimal? expected,
        decimal? actual,
        decimal? delta
    )
    {
        var count = value.Currency == "XXX";
        AuditValue? Typed(decimal? n)
        {
            if (n is null)
            {
                return null;
            }
            return count && n.Value == decimal.Truncate(n.Value) && n.Value is >= long.MinValue and <= long.MaxValue
                ? ComparisonSupport.Integer((long)n.Value)
                : ComparisonSupport.Money(n.Value);
        }
        return ComparisonSupport.Finding(
            Code,
            rule,
            value.GroupKey,
            [],
            value.Field,
            Typed(expected),
            Typed(actual),
            Typed(delta),
            count ? ValueUnit.Count : ValueUnit.Money,
            count ? null : value.Currency,
            AccountingBasis.None
        );
    }
}
