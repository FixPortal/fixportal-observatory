namespace AiObservatory.Audit.Contracts;

/// <summary>Deterministic scope deliberately excludes operational predecessor and evaluation run IDs.</summary>
public sealed record AuditResultScope(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<SourceSelection> Sources,
    IReadOnlyList<CheckCode> RequestedChecks,
    IReadOnlyList<CheckCode> ExcludedChecks,
    bool AllowPartial
)
{
    public static AuditResultScope FromScope(AuditScope scope) =>
        new(scope.From, scope.To, scope.Sources, scope.RequestedChecks, scope.ExcludedChecks, scope.AllowPartial);
}
