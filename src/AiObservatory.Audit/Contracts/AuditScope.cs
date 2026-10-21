namespace AiObservatory.Audit.Contracts;

public sealed record AuditScope(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<SourceSelection> Sources,
    IReadOnlyList<CheckCode> RequestedChecks,
    IReadOnlyList<CheckCode> ExcludedChecks,
    bool AllowPartial,
    Guid? PredecessorRunId = null,
    Guid? EvaluationOfRunId = null
);
