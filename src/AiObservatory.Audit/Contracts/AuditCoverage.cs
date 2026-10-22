namespace AiObservatory.Audit.Contracts;

public sealed record AuditCoverage(long ComparedGroups, long TotalGroups, IReadOnlyList<CoverageGap> Gaps)
{
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    )]
    public bool? DetailsRetained { get; init; }
    public IReadOnlyList<CoverageExclusion> Exclusions { get; init; } = [];
}

public sealed record CoverageGap(string SourceId, string GroupKey, string DiagnosticCode);
