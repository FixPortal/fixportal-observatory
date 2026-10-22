using AiObservatory.Audit.Contracts;

namespace AiObservatory.Audit.Checks;

public sealed class BillingReconciliationCheck
{
    private readonly CheckCode _code = CheckCode.BillingReconciliation;

    public CheckOutcome Evaluate(AuditInput input) =>
        input.Scope.ExcludedChecks.Contains(_code)
            ? ComparisonSupport.Excluded(CheckCode.BillingReconciliation)
            : new(
                CheckCode.BillingReconciliation,
                CheckVerdict.Unsupported,
                new(0, 1, [new("all", "all", "billing_feed_unqualified")]),
                []
            );
}
