namespace AiObservatory.Audit.Contracts;

public sealed record SourceReconstruction(
    IReadOnlyList<ReconstructedGroup> CompatibleGroups,
    IReadOnlyList<ReconstructedGroup> ChronologicalGroups,
    AuditCoverage Coverage,
    IReadOnlyList<AuditFinding> Findings
);

public sealed record ReconstructedGroup(
    AccountingKey Key,
    UsageCounters Tokens,
    decimal? Cost,
    string? Currency,
    bool Provisional,
    IReadOnlyList<string> EvidenceRefs
);

/// <summary>Source accounting additionally retains machine/generation/session and model pricing dimensions.</summary>
public sealed record AccountingKey(
    Guid MachineId,
    string SourceId,
    string Generation,
    string? SessionId,
    DateOnly Date,
    string Provider,
    string? Model,
    ObservationProvenance Provenance,
    PricingDimensions Pricing
);
