namespace AiObservatory.Audit.Contracts;

/// <summary>Values are typed numeric strings; diagnostics and references are sanitized tokens, never prose.</summary>
public sealed record AuditFinding(
    CheckCode CheckCode,
    string RuleCode,
    string GroupKey,
    IReadOnlyList<string> EvidenceRefs,
    string Field,
    AuditValue? Expected,
    AuditValue? Actual,
    AuditValue? Delta,
    ValueUnit Unit,
    string? Currency,
    AccountingBasis Basis,
    string DiagnosticCode
);

public sealed record AuditValue(AuditValueKind Kind, string Value);

public enum AuditValueKind
{
    Integer,
    Decimal,
}
