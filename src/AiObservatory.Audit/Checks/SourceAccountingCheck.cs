using AiObservatory.Audit.Contracts;

namespace AiObservatory.Audit.Checks;

public sealed class SourceAccountingCheck
{
    private readonly SourceCheckEvaluation _evaluation = new();

    public CheckOutcome Evaluate(AuditInput input) => _evaluation.Evaluate(input, CheckCode.SourceAccounting);
}
