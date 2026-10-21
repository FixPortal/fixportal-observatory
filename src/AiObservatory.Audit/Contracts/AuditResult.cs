namespace AiObservatory.Audit.Contracts;

public sealed record AuditResult(
    int SchemaVersion,
    AuditResultScope Scope,
    string InputContentDigest,
    string VerifierVersion,
    string RuleVersion,
    IReadOnlyList<CheckOutcome> Checks,
    AuditCoverage Coverage,
    IReadOnlyList<AuditFinding> Findings,
    CheckVerdict Verdict
);
