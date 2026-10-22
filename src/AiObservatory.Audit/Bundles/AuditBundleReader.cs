using System.IO.Compression;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;

namespace AiObservatory.Audit.Bundles;

public static class AuditBundleReader
{
    public static async Task<AuditInput> ReadAsync(Stream source, BundleLimits limits, CancellationToken ct) =>
        (await ReadBundleAsync(source, limits, ct)).Input;

    public static async Task<AuditBundle> ReadBundleAsync(Stream source, BundleLimits limits, CancellationToken ct)
    {
        BundleCodec.ValidateLimits(limits);
        try
        {
            using var buffered = await ReadBounded(source, BundleCodec.HardTotalBytes + 16 * 1024 * 1024, ct);
            BundleZipDirectory.Validate(buffered, limits);
            using var archive = new ZipArchive(buffered, ZipArchiveMode.Read, leaveOpen: true);
            ValidateDirectory(archive, limits);
            var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var entry in archive.Entries)
            {
                await using var stream = await entry.OpenAsync(ct);
                using var bytes = await ReadBounded(stream, entry.Length, ct);
                BundleCodec.Require(bytes.Length == entry.Length, "bundle_entry_length_mismatch");
                entries.Add(entry.FullName, bytes.ToArray());
            }
            return Decode(entries, limits);
        }
        catch (InvalidDataException)
        {
            throw new AuditValidationException("invalid_bundle_zip");
        }
        catch (IOException)
        {
            throw new AuditValidationException("bundle_io_failure");
        }
    }

    private static void ValidateDirectory(ZipArchive archive, BundleLimits limits)
    {
        BundleCodec.Require(
            archive.Entries.Count is > 0 && archive.Entries.Count <= limits.MaxEntries,
            "bundle_entry_limit"
        );
        var paths = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            BundleCodec.Require(BundleCodec.IsAllowedPath(entry.FullName), "unknown_bundle_path");
            BundleCodec.Require(paths.Add(entry.FullName), "duplicate_bundle_path");
            BundleCodec.Require(entry.Length >= 0 && entry.Length <= limits.MaxEntryBytes, "bundle_entry_size_limit");
            var bound = EntryBound(entry.FullName, limits);
            BundleCodec.Require(entry.Length <= bound, "bundle_entry_size_limit");
            total += entry.Length;
            BundleCodec.Require(total <= limits.MaxTotalBytes, "bundle_size_limit");
            BundleCodec.Require(
                entry.Length <= Math.Max(1, entry.CompressedLength) * limits.MaxCompressionRatio,
                "bundle_compression_limit"
            );
        }
        BundleCodec.Require(paths.Contains("manifest.json"), "missing_bundle_manifest");
    }

    private static long EntryBound(string path, BundleLimits limits) =>
        path switch
        {
            "manifest.json" or "options.json" => BundleCodec.ManifestBytes,
            _ when path.EndsWith("/manifest.json", StringComparison.Ordinal) => BundleCodec.ManifestBytes,
            _ when path.Contains("/chunks/", StringComparison.Ordinal) => limits.MaxChunkBytes,
            _ => limits.MaxEntryBytes,
        };

    private static AuditBundle Decode(Dictionary<string, byte[]> entries, BundleLimits limits)
    {
        var manifest = AuditCanonicalJson.DeserializeEnvelope<AuditBundleManifest>(entries["manifest.json"]);
        ValidateManifest(manifest, entries, limits);
        var options = AuditCanonicalJson.DeserializeEnvelope<BundleOptions>(entries["options.json"]);
        BundleCodec.Require(
            options.VerifierVersion == manifest.VerifierVersion && options.RuleVersion == manifest.RuleVersion,
            "bundle_version_mismatch"
        );
        var captures = entries
            .Keys.Where(p =>
                p.StartsWith("captures/", StringComparison.Ordinal)
                && p.EndsWith("/manifest.json", StringComparison.Ordinal)
            )
            .Order(StringComparer.Ordinal)
            .Select(path => ReadCapture(path, entries))
            .ToArray();
        ValidateEntrySet(entries, captures);
        var input = new AuditInput
        {
            SchemaVersion = options.SchemaVersion,
            VerifierVersion = options.VerifierVersion,
            RuleVersion = options.RuleVersion,
            Scope = options.Scope,
            Options = options.Options,
            Tolerances = options.Tolerances,
            PresentationProbe = options.PresentationProbe,
            SourceQualifications = options.SourceQualifications,
            FinancialObservations = entries.TryGetValue(BundleCodec.FinancialPath, out var financial)
                ? BundleCodec.ParseNdjson<FrozenFinancialObservation>(financial)
                : null,
            PartialEvidenceAcknowledged = options.PartialEvidenceAcknowledged,
            Captures = captures.Select(c => c.Manifest).ToArray(),
            Records = captures
                .SelectMany(c => c.Chunks.SelectMany(chunk => BundleCodec.ParseNdjson<SourceUsageRecord>(chunk.Ndjson)))
                .ToArray(),
            Events = BundleCodec.ParseNdjson<FrozenEvent>(entries["snapshot/events.ndjson"]),
            Aggregates = BundleCodec.ParseNdjson<FrozenAggregate>(entries["snapshot/aggregates.ndjson"]),
            Spend = BundleCodec.ParseNdjson<FrozenSpend>(entries["snapshot/spend.ndjson"]),
            SourceStatuses = BundleCodec.ParseNdjson<FrozenSourceStatus>(entries["snapshot/source-statuses.ndjson"]),
            FxRates = BundleCodec.ParseNdjson<FrozenFx>(entries["snapshot/fx.ndjson"]),
            PricingCatalogs = AuditCanonicalJson.Deserialize<FrozenPricing[]>(entries["pricing/catalogs.json"]),
        };
        var bundle = new AuditBundle(
            input,
            AuditCanonicalJson.Deserialize<AuditResult>(entries["result.json"]),
            captures
        );
        return bundle with { Input = BundleCodec.ValidateEvidence(bundle, limits) };
    }

    private static void ValidateManifest(
        AuditBundleManifest manifest,
        Dictionary<string, byte[]> entries,
        BundleLimits limits
    )
    {
        BundleCodec.Require(manifest.SchemaVersion == 1, "unsupported_bundle_schema");
        BundleCodec.Require(
            limits.AllowReevaluation
                || manifest.VerifierVersion == AuditVerifierIdentity.VerifierVersion
                    && manifest.RuleVersion == AuditVerifierIdentity.RuleVersion,
            "incompatible_bundle_verifier"
        );
        BundleCodec.Require(manifest.Entries.Count == entries.Count - 1, "bundle_entry_set_mismatch");
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in manifest.Entries)
        {
            BundleCodec.Require(
                entry != null && entry.Path != "manifest.json" && paths.Add(entry.Path),
                "duplicate_bundle_declaration"
            );
            BundleCodec.Require(entries.TryGetValue(entry!.Path, out var bytes), "missing_bundle_entry");
            BundleCodec.Require(
                entry.Length == bytes!.LongLength && entry.Sha256 == AuditCanonicalJson.ContentHash(bytes),
                "bundle_entry_hash_mismatch"
            );
        }
        BundleCodec.Require(BundleCodec.FixedPaths.All(entries.ContainsKey), "missing_bundle_entry");
        BundleCodec.Require(
            manifest.ContentHash
                == AuditCanonicalJson.ContentHash(AuditCanonicalJson.SerializeEnvelope(manifest.Entries)),
            "bundle_content_hash_mismatch"
        );
    }

    private static AuditCaptureEvidence ReadCapture(string path, Dictionary<string, byte[]> entries)
    {
        var manifest = AuditCanonicalJson.Deserialize<CaptureManifest>(entries[path]);
        BundleCodec.Require(path == BundleCodec.CapturePath(manifest.CaptureId), "capture_path_identity_mismatch");
        BundleCodec.Require(manifest.ChunkHashes.Count <= BundleCodec.HardEntries, "bundle_entry_limit");
        var chunks = manifest
            .ChunkHashes.Select(
                (_, index) =>
                {
                    BundleCodec.Require(
                        entries.TryGetValue(BundleCodec.ChunkPath(manifest.CaptureId, index), out var bytes),
                        "missing_capture_chunk"
                    );
                    return new AuditCaptureChunk(index, bytes!);
                }
            )
            .ToArray();
        return new(manifest, chunks);
    }

    private static void ValidateEntrySet(
        Dictionary<string, byte[]> entries,
        IReadOnlyList<AuditCaptureEvidence> captures
    )
    {
        var expected = new HashSet<string>(BundleCodec.FixedPaths, StringComparer.Ordinal) { "manifest.json" };
        if (entries.ContainsKey(BundleCodec.FinancialPath))
        {
            expected.Add(BundleCodec.FinancialPath);
        }
        foreach (var capture in captures)
        {
            expected.Add(BundleCodec.CapturePath(capture.Manifest.CaptureId));
            foreach (var chunk in capture.Chunks)
            {
                expected.Add(BundleCodec.ChunkPath(capture.Manifest.CaptureId, chunk.Index));
            }
        }
        BundleCodec.Require(expected.SetEquals(entries.Keys), "bundle_entry_set_mismatch");
    }

    private static async Task<MemoryStream> ReadBounded(Stream source, long maximum, CancellationToken ct)
    {
        var result = new MemoryStream();
        try
        {
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                BundleCodec.Require(result.Length + read <= maximum, "bundle_size_limit");
                await result.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            result.Position = 0;
            return result;
        }
        catch
        {
            await result.DisposeAsync();
            throw;
        }
    }
}
