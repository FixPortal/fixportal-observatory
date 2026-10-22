using AiObservatory.Audit.Contracts;

namespace AiObservatory.Audit.Checks;

public static class AuditVerdictReducer
{
    public static CheckVerdict Reduce(IReadOnlyList<CheckOutcome> checks)
    {
        var executed = checks.Where(c => c.Verdict != CheckVerdict.Excluded).ToArray();
        if (executed.Any(c => c.Verdict == CheckVerdict.Mismatched))
        {
            return CheckVerdict.Mismatched;
        }
        return
            executed.Length == 0 || executed.Any(c => c.Verdict is CheckVerdict.Incomplete or CheckVerdict.Unsupported)
            ? CheckVerdict.Incomplete
            : CheckVerdict.Matched;
    }
}
