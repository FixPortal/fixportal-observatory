namespace AiObservatory.Audit.Contracts;

public sealed record CaptureManifest(
    int SchemaVersion,
    Guid CaptureId,
    Guid RequestId,
    Guid MachineId,
    string Generation,
    string ExtractorVersion,
    DateTimeOffset ScanStartedAt,
    DateTimeOffset ScanEndedAt,
    AuditScope Scope,
    IReadOnlyList<SourceFormat> SourceFormats,
    long DiscoveredFiles,
    long ReadFiles,
    long SkippedFiles,
    long ChangedFiles,
    long RecordCount,
    DateOnly? ObservedFrom,
    DateOnly? ObservedTo,
    IReadOnlyList<DiagnosticCount> Diagnostics,
    IReadOnlyList<string> ChunkHashes,
    DateTimeOffset SealedAt
);

public sealed record SourceFormat(string SourceId, string Format, string Version);

public sealed record DiagnosticCount(string Code, long Count);
