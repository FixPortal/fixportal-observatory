namespace AiObservatory.Audit.Contracts;

/// <summary>A field outside a check's declared scope, rather than missing evidence for an included field.</summary>
public sealed record CoverageExclusion(string Field, string DiagnosticCode);
