using System.IO.Compression;
using AiObservatory.Audit.Serialization;

namespace AiObservatory.Audit.Bundles;

public static class AuditBundleWriter
{
    public static void Validate(AuditBundle bundle) => _ = BundleCodec.ValidateEvidence(bundle, new BundleLimits());

    public static async Task WriteAsync(Stream destination, AuditBundle bundle, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var limits = new BundleLimits();
        var input = BundleCodec.ValidateEvidence(bundle, limits);
        var entries = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["options.json"] = AuditCanonicalJson.SerializeEnvelope(
                new BundleOptions(
                    input.SchemaVersion,
                    input.VerifierVersion,
                    input.RuleVersion,
                    input.Scope,
                    input.Options,
                    input.Tolerances,
                    input.PresentationProbe
                )
                {
                    SourceQualifications = input.SourceQualifications,
                    PartialEvidenceAcknowledged = input.PartialEvidenceAcknowledged,
                }
            ),
            ["result.json"] = AuditCanonicalJson.Serialize(bundle.Result),
            ["snapshot/events.ndjson"] = BundleCodec.Ndjson(input.Events),
            ["snapshot/aggregates.ndjson"] = BundleCodec.Ndjson(input.Aggregates),
            ["snapshot/spend.ndjson"] = BundleCodec.Ndjson(input.Spend),
            ["snapshot/source-statuses.ndjson"] = BundleCodec.Ndjson(input.SourceStatuses),
            ["snapshot/fx.ndjson"] = BundleCodec.Ndjson(input.FxRates),
            ["pricing/catalogs.json"] = AuditCanonicalJson.Serialize(input.PricingCatalogs),
        };
        if (input.FinancialObservations != null)
        {
            entries.Add(BundleCodec.FinancialPath, BundleCodec.Ndjson(input.FinancialObservations));
        }
        foreach (var capture in bundle.Captures)
        {
            entries.Add(
                BundleCodec.CapturePath(capture.Manifest.CaptureId),
                AuditCanonicalJson.Serialize(capture.Manifest)
            );
            foreach (var chunk in capture.Chunks)
            {
                entries.Add(BundleCodec.ChunkPath(capture.Manifest.CaptureId, chunk.Index), chunk.Ndjson);
            }
        }
        BundleCodec.Require(
            entries
                .Where(e => e.Key == "options.json" || e.Key.EndsWith("/manifest.json", StringComparison.Ordinal))
                .All(e => e.Value.Length <= BundleCodec.ManifestBytes),
            "bundle_metadata_limit"
        );
        var declarations = entries
            .Select(e => new AuditBundleEntry(e.Key, e.Value.LongLength, AuditCanonicalJson.ContentHash(e.Value)))
            .ToArray();
        var manifest = new AuditBundleManifest(
            1,
            input.VerifierVersion,
            input.RuleVersion,
            AuditCanonicalJson.ContentHash(AuditCanonicalJson.SerializeEnvelope(declarations)),
            declarations
        );
        var manifestBytes = AuditCanonicalJson.SerializeEnvelope(manifest);
        BundleCodec.Require(manifestBytes.Length <= BundleCodec.ManifestBytes, "bundle_manifest_limit");
        entries.Add("manifest.json", manifestBytes);
        BundleCodec.Require(
            entries.Count <= limits.MaxEntries && entries.Values.Sum(e => (long)e.Length) <= limits.MaxTotalBytes,
            "bundle_size_limit"
        );
        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var (path, bytes) in entries)
        {
            // Stored entries avoid compression ratio ambiguity and preserve byte-for-byte evidence.
            var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
            entry.LastWriteTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            await using var output = await entry.OpenAsync(ct);
            await output.WriteAsync(bytes, ct);
        }
    }
}
