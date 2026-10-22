using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Pricing;

namespace AiObservatory.Audit.Checks;

public sealed class PricingArithmeticCheck
{
    private const CheckCode Code = CheckCode.PricingArithmetic;

    public CheckOutcome Evaluate(AuditInput input)
    {
        input = ComparisonSupport.Normalize(input);
        if (input.Scope.ExcludedChecks.Contains(Code))
        {
            return ComparisonSupport.Excluded(Code);
        }
        var events = ComparisonSupport.Events(input);
        var gaps = new List<CoverageGap>();
        if (events.Count == 0)
        {
            gaps.Add(new("pricing", "pricing", "no_pricing_evidence"));
        }
        var findings = new List<AuditFinding>();
        var traces = new List<PricingEquationTrace>();
        var compared = events.LongCount(e => EvaluateEvent(input, e, gaps, findings, traces));
        var unsupported = events.Count > 0 && events.All(e => !Supported(e));
        return new(
            Code,
            unsupported ? CheckVerdict.Unsupported : ComparisonSupport.Verdict(findings.Count, gaps.Count),
            new(compared, events.Count, gaps),
            findings
        )
        {
            PricingTraces = traces.Count == 0 ? null : traces,
        };
    }

    private static bool Supported(FrozenEvent e) =>
        e.Provider.Equals("OpenAi", StringComparison.OrdinalIgnoreCase)
        || e.Provider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase);

    private static bool EvaluateEvent(
        AuditInput input,
        FrozenEvent e,
        List<CoverageGap> gaps,
        List<AuditFinding> findings,
        List<PricingEquationTrace> traces
    )
    {
        var key = e.RowId.ToString("D");
        void Gap(string code) => gaps.Add(new(e.SourceId, key, code));
        if (!Supported(e))
        {
            Gap("provider_formula_unsupported");
            return false;
        }
        if (e.PricingApplication is { Kind: "ruleDerived" })
        {
            return EvaluateRuleDerived(input, e, Gap, findings);
        }
        // Frozen typed catalogs do not contain the underlying first-party publication evidence.
        Gap("historical_rate_evidence_missing");
        if (e.PricingApplication is not { Kind: "catalog" })
        {
            Gap(e.CorrectedAt != null ? "manual_correction_not_catalog" : "pricing_application_missing");
            var fallback = FrozenRateResolver.DeclaredFallback(input, e);
            if (fallback == null)
            {
                return false;
            }
            Gap("declared_catalog_fallback");
            e = fallback.Value.Event;
        }
        var app = e.PricingApplication!;
        if (app.Dimensions.Tier == ServiceTier.Unknown && !app.Dimensions.Batch)
        {
            Gap("pricing_dimensions_unknown");
            return false;
        }
        var catalog = FrozenRateResolver.Catalog(input, e, out var catalogDiagnostic);
        if (catalog == null || catalog.Currency != "USD")
        {
            Gap(catalogDiagnostic ?? "applied_catalog_missing");
            return false;
        }
        QualifySource(input, catalog, Gap);
        if (!ValidSubsets(e, findings))
        {
            return false;
        }
        var terms = Terms(e, Gap);
        if (terms == null)
        {
            return false;
        }
        return CompareTerms(input, e, catalog, terms, gaps, findings, traces);
    }

    private static bool EvaluateRuleDerived(
        AuditInput input,
        FrozenEvent e,
        Action<string> gap,
        List<AuditFinding> findings
    )
    {
        var application = e.PricingApplication!;
        if (application.RuleId != "exact-zero" || application.RuleVersion != "1")
        {
            gap("pricing_rule_unsupported");
            return false;
        }
        if (application.SnapshotId != null || application.SnapshotHash != null)
        {
            gap("exact_zero_rule_provenance_invalid");
            return false;
        }
        var counters = e.Tokens;
        if (
            counters.InputTokens != 0
            || counters.OutputTokens != 0
            || counters.CacheReadTokens != 0
            || counters.CacheWriteTokens != 0
            || counters.CacheWrite1hTokens != 0
            || counters.ThoughtTokens != 0
        )
        {
            gap("exact_zero_rule_counters_not_zero");
            return false;
        }
        var costCompared = CompareExactZeroValue(input, e, "costUsd", e.CostUsd, gap, findings);
        var savingsCompared = CompareExactZeroValue(input, e, "cacheSavingsUsd", e.CacheSavingsUsd, gap, findings);
        return costCompared && savingsCompared;
    }

    private static bool CompareExactZeroValue(
        AuditInput input,
        FrozenEvent e,
        string field,
        decimal? actual,
        Action<string> gap,
        List<AuditFinding> findings
    )
    {
        if (actual == null)
        {
            gap(field == "costUsd" ? "production_cost_missing" : "production_cache_savings_missing");
            return false;
        }
        var tolerance =
            input.Tolerances.SingleOrDefault(t => t.Name == "pricingArithmetic." + field)?.AbsoluteTolerance ?? 0m;
        if (actual.Value > tolerance || actual.Value < -tolerance)
        {
            var key = e.RowId.ToString("D");
            findings.Add(
                ComparisonSupport.Finding(
                    Code,
                    field == "costUsd" ? "pricing_cost_difference" : "pricing_savings_difference",
                    key,
                    [key],
                    field,
                    ComparisonSupport.Money(0m),
                    ComparisonSupport.Money(actual.Value),
                    ComparisonSupport.Money(actual.Value),
                    ValueUnit.Money,
                    "USD",
                    e.Provenance.CostBasis
                )
            );
        }
        return true;
    }

    private static void QualifySource(AuditInput input, FrozenPricing catalog, Action<string> gap)
    {
        var status = input.SourceStatuses.SingleOrDefault(s => s.SourceId == catalog.SourceId);
        if (status == null)
        {
            return;
        }
        if (!status.IsConfigured || status.IsAvailable == false)
        {
            gap("pricing_source_unavailable");
        }
        if (status.LastSuccessAt == null)
        {
            gap("pricing_source_last_success_unknown");
        }
        else if (
            status.LastAttemptAt is { } attempted
            && status.ExpectedRefreshIntervalSeconds > 0
            && (attempted.UtcTicks - status.LastSuccessAt.Value.UtcTicks) / TimeSpan.TicksPerSecond
                > status.ExpectedRefreshIntervalSeconds
        )
        {
            gap("pricing_source_stale");
        }
    }

    private static bool ValidSubsets(FrozenEvent e, List<AuditFinding> findings)
    {
        var total = e.Tokens.CacheWriteTokens ?? 0;
        var hour = e.Tokens.CacheWrite1hTokens ?? 0;
        if (hour <= total)
        {
            return true;
        }
        var key = e.RowId.ToString("D");
        findings.Add(
            ComparisonSupport.Finding(
                Code,
                "cache_write_subset_invalid",
                key,
                [key],
                "cacheWrite1hTokens",
                ComparisonSupport.Integer(total),
                ComparisonSupport.Integer(hour),
                ComparisonSupport.Integer(checked(hour - total)),
                ValueUnit.Tokens,
                null,
                e.Provenance.CostBasis
            )
        );
        return false;
    }

    private static IReadOnlyList<(PriceMeter Meter, long Tokens)>? Terms(FrozenEvent e, Action<string> Gap)
    {
        IReadOnlyList<(PriceMeter Meter, long Tokens)>? terms;
        if (e.Provider.Equals("OpenAi", StringComparison.OrdinalIgnoreCase))
        {
            terms = OpenAiAuditPricing.Terms(e);
        }
        else
        {
            terms = ClaudeAuditPricing.Terms(e, out var diagnostic);
            if (diagnostic != null)
            {
                Gap(diagnostic);
            }
        }
        return terms;
    }

    private static bool CompareTerms(
        AuditInput input,
        FrozenEvent e,
        FrozenPricing catalog,
        IReadOnlyList<(PriceMeter Meter, long Tokens)> terms,
        List<CoverageGap> gaps,
        List<AuditFinding> findings,
        List<PricingEquationTrace> traces
    )
    {
        var key = e.RowId.ToString("D");
        void Gap(string code) => gaps.Add(new(e.SourceId, key, code));
        var eventTraces = new List<PricingEquationTrace>();
        foreach (var (meter, tokens) in terms.Where(t => t.Tokens != 0))
        {
            var rate = FrozenRateResolver.Rate(catalog, e, meter);
            if (rate == null || rate.UnitTokens <= 0 || rate.UnitPrice < 0 || rate.RegionMultiplier is <= 0)
            {
                Gap("applied_rate_missing");
                return false;
            }
            if (!rate.EffectiveDateIsProviderDeclared)
            {
                Gap("catalog_effective_date_assumed");
            }
            var multiplier = rate.RegionMultiplier ?? 1m;
            decimal amount;
            try
            {
                amount = checked(tokens * rate.UnitPrice * multiplier / rate.UnitTokens);
            }
            catch (OverflowException)
            {
                throw new AuditValidationException("money_overflow");
            }
            eventTraces.Add(
                new(
                    key,
                    catalog.Id,
                    rate.Id,
                    meter,
                    tokens,
                    rate.UnitTokens,
                    rate.UnitPrice,
                    multiplier,
                    amount,
                    catalog.Currency
                )
            );
        }
        traces.AddRange(eventTraces);
        var expected = ComparisonSupport.Sum(eventTraces.Select(t => t.Amount));
        if (e.CostUsd == null)
        {
            Gap("production_cost_missing");
            return false;
        }
        var delta = ComparisonSupport.Difference(expected, e.CostUsd.Value);
        var tolerance =
            input.Tolerances.SingleOrDefault(t => t.Name == "pricingArithmetic.costUsd")?.AbsoluteTolerance ?? 0m;
        if (delta > tolerance || delta < -tolerance)
        {
            findings.Add(
                ComparisonSupport.Finding(
                    Code,
                    "pricing_cost_difference",
                    key,
                    [key],
                    "costUsd",
                    ComparisonSupport.Money(expected),
                    ComparisonSupport.Money(e.CostUsd.Value),
                    ComparisonSupport.Money(delta),
                    ValueUnit.Money,
                    "USD",
                    e.Provenance.CostBasis
                )
            );
        }
        CompareSavings(input, e, catalog, eventTraces, findings);
        return true;
    }

    private static void CompareSavings(
        AuditInput input,
        FrozenEvent e,
        FrozenPricing catalog,
        List<PricingEquationTrace> traces,
        List<AuditFinding> findings
    )
    {
        if (e.CacheSavingsUsd == null)
        {
            return;
        }
        var rate = FrozenRateResolver.Rate(catalog, e, PriceMeter.Input);
        if (rate == null || rate.UnitTokens <= 0)
        {
            return;
        }
        var cached = traces
            .Where(t => t.Meter is PriceMeter.CacheRead or PriceMeter.CacheWrite5m or PriceMeter.CacheWrite1h)
            .ToArray();
        var tokens = ComparisonSupport.CountSum(cached.Select(t => t.Tokens));
        decimal expected;
        try
        {
            expected = checked(
                tokens * rate.UnitPrice * (rate.RegionMultiplier ?? 1m) / rate.UnitTokens
                - ComparisonSupport.Sum(cached.Select(t => t.Amount))
            );
        }
        catch (OverflowException)
        {
            throw new AuditValidationException("money_overflow");
        }
        var delta = ComparisonSupport.Difference(expected, e.CacheSavingsUsd.Value);
        var tolerance =
            input.Tolerances.SingleOrDefault(t => t.Name == "pricingArithmetic.cacheSavingsUsd")?.AbsoluteTolerance
            ?? 0m;
        if (delta > tolerance || delta < -tolerance)
        {
            var key = e.RowId.ToString("D");
            findings.Add(
                ComparisonSupport.Finding(
                    Code,
                    "pricing_savings_difference",
                    key,
                    [key],
                    "cacheSavingsUsd",
                    ComparisonSupport.Money(expected),
                    ComparisonSupport.Money(e.CacheSavingsUsd.Value),
                    ComparisonSupport.Money(delta),
                    ValueUnit.Money,
                    "USD",
                    e.Provenance.CostBasis
                )
            );
        }
    }
}
