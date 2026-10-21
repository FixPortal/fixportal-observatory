using AiObservatory.Audit.Contracts;

namespace AiObservatory.Audit.Checks;

public sealed class DailyAttributionCheck
{
    private readonly SourceCheckEvaluation evaluation = new();

    public CheckOutcome Evaluate(AuditInput input) => evaluation.Evaluate(input, CheckCode.DailyAttribution);
}
