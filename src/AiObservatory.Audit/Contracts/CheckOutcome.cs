namespace AiObservatory.Audit.Contracts;

public sealed record CheckOutcome(
    CheckCode Code,
    CheckVerdict Verdict,
    AuditCoverage Coverage,
    IReadOnlyList<AuditFinding> Findings
);
