using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Sources;

namespace AiObservatory.Api.Audit;

/// <summary>Qualifications are frozen input facts, so portable replay makes the same bounded claim.</summary>
public static class AuditCompatibilityEvaluator
{
    public static AuditInput Qualify(AuditInput input)
    {
        var qualifications = input
            .Scope.Sources.Select(s => s.SourceId)
            .Distinct()
            .Select(source =>
            {
                var captures = input.Captures.Where(c => c.Scope.Sources.Any(s => s.SourceId == source)).ToArray();
                if (
                    input
                        .Scope.Sources.Where(s => s.SourceId == source)
                        .Any(s =>
                            !captures.Any(c =>
                                c.MachineId == s.MachineId
                                && c.Scope.From <= input.Scope.From
                                && c.Scope.To >= input.Scope.To
                            )
                        )
                )
                {
                    return new SourceQualification(source, "capture_missing", false);
                }
                if (
                    captures.Any(c =>
                        c.ReadFiles != c.DiscoveredFiles
                        || c.SkippedFiles > 0
                        || c.ChangedFiles > 0
                        || c.Diagnostics.Any(d => d.Count > 0)
                    )
                )
                {
                    return new SourceQualification(source, "incomplete_capture", false);
                }
                if (
                    captures.Any(c =>
                        c.SourceFormats.Any(f =>
                            f.SourceId == source
                            && !AuditCapabilities.Sources.Any(a =>
                                a.Supported && a.Format == f.Format && a.Version == f.Version
                            )
                        )
                    )
                )
                {
                    return new SourceQualification(source, "unsupported_source_format", false);
                }
                // A later sweep timestamp proves ordering, not a changed native revision.
                // Current retained protocol facts cannot prove native capture/server alignment.
                return new SourceQualification(source, "revision_unverified", true);
            })
            .ToArray();
        return input with { SourceQualifications = qualifications };
    }
}
