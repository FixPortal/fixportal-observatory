using AiObservatory.Audit.Checks;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;

namespace AiObservatory.Audit;

public sealed class AuditVerifier
{
    public AuditResult Verify(AuditInput input)
    {
        input = AuditComputationInput.Normalize(input);
        var checks = input
            .Scope.RequestedChecks.Concat(input.Scope.ExcludedChecks)
            .Distinct()
            .Select(code => Evaluate(input, code))
            .ToArray();
        CoverageGap[] evidenceGaps =
            input.PartialEvidenceAcknowledged == true ? [new("all", "all", "partial_capture_acknowledged")] : [];
        var coverage = new AuditCoverage(
            ComparisonSupport.CountSum(checks.Select(c => c.Coverage.ComparedGroups)),
            ComparisonSupport.CountSum(checks.Select(c => c.Coverage.TotalGroups)),
            checks.SelectMany(c => c.Coverage.Gaps).Concat(evidenceGaps).Distinct().ToArray()
        )
        {
            Exclusions = checks.SelectMany(c => c.Coverage.Exclusions).Distinct().ToArray(),
        };
        var verdict = AuditVerdictReducer.Reduce(checks);
        if (verdict == CheckVerdict.Matched && evidenceGaps.Length > 0)
        {
            verdict = CheckVerdict.Incomplete;
        }
        var result = new AuditResult(
            1,
            AuditResultScope.FromScope(input.Scope),
            AuditComputationInput.ContentDigest(input),
            input.VerifierVersion,
            input.RuleVersion,
            checks,
            coverage,
            checks.SelectMany(c => c.Findings).ToArray(),
            verdict
        );
        return AuditCanonicalJson.Deserialize<AuditResult>(AuditCanonicalJson.Serialize(result));
    }

    private static CheckOutcome Evaluate(AuditInput input, CheckCode code)
    {
        if (input.Scope.ExcludedChecks.Contains(code))
        {
            return ComparisonSupport.Excluded(code);
        }
        return code switch
        {
            CheckCode.SourceAccounting => new SourceAccountingCheck().Evaluate(input),
            CheckCode.DailyAttribution => new DailyAttributionCheck().Evaluate(input),
            CheckCode.EventAggregation => new EventAggregationCheck().Evaluate(input),
            CheckCode.PresentationTotals => new PresentationTotalsCheck().Evaluate(input),
            CheckCode.PricingArithmetic => new PricingArithmeticCheck().Evaluate(input),
            CheckCode.BillingReconciliation => new BillingReconciliationCheck().Evaluate(input),
            _ => new(code, CheckVerdict.Unsupported, new(0, 1, [new("all", "all", "check_not_implemented")]), []),
        };
    }
}
