namespace AiObservatory.Audit.Contracts;

public sealed record AuditCoverage(long ComparedGroups, long TotalGroups, IReadOnlyList<CoverageGap> Gaps)
{
    public IReadOnlyList<CoverageExclusion> Exclusions { get; init; } = [];
}

public sealed record CoverageGap(string SourceId, string GroupKey, string DiagnosticCode);
