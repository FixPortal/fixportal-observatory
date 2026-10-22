using AiObservatory.Audit.Contracts;

namespace AiObservatory.Audit.Bundles;

public sealed record AuditBundleManifest(
    int SchemaVersion,
    string VerifierVersion,
    string RuleVersion,
    string ContentHash,
    IReadOnlyList<AuditBundleEntry> Entries
);

public sealed record AuditBundleEntry(string Path, long Length, string Sha256);

/// <summary>Chunks are retained exact wire bytes; global Records cannot reconstruct capture membership.</summary>
public sealed record AuditBundle(AuditInput Input, AuditResult Result, IReadOnlyList<AuditCaptureEvidence> Captures);

public sealed record AuditCaptureEvidence(CaptureManifest Manifest, IReadOnlyList<AuditCaptureChunk> Chunks);

public sealed record AuditCaptureChunk(int Index, byte[] Ndjson);

public sealed record BundleLimits
{
    public long MaxTotalBytes { get; init; } = 512L * 1024 * 1024;
    public long MaxEntryBytes { get; init; } = 512L * 1024 * 1024;
    public int MaxChunkBytes { get; init; } = 8 * 1024 * 1024;
    public int MaxEntries { get; init; } = 10_000;
    public int MaxCompressionRatio { get; init; } = 200;
    public bool AllowReevaluation { get; init; }
}
