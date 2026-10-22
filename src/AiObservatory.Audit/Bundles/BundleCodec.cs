using System.Globalization;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;

namespace AiObservatory.Audit.Bundles;

internal static class BundleCodec
{
    internal const long HardTotalBytes = 512L * 1024 * 1024;
    internal const int HardChunkBytes = 8 * 1024 * 1024;
    internal const int HardEntries = 10_000;
    internal const int ManifestBytes = 1024 * 1024;

    internal static readonly string[] FixedPaths =
    [
        "options.json",
        "result.json",
        "snapshot/events.ndjson",
        "snapshot/aggregates.ndjson",
        "snapshot/spend.ndjson",
        "snapshot/source-statuses.ndjson",
        "snapshot/fx.ndjson",
        "pricing/catalogs.json",
    ];

    internal static string CapturePath(Guid id) => $"captures/{id:D}/manifest.json";

    internal static string ChunkPath(Guid id, int index) =>
        $"captures/{id:D}/chunks/{index.ToString("D6", CultureInfo.InvariantCulture)}.ndjson";

    internal static void Require(bool condition, string code)
    {
        if (!condition)
        {
            throw new AuditValidationException(code);
        }
    }

    internal static void ValidateLimits(BundleLimits limits)
    {
        Require(limits.MaxTotalBytes is > 0 and <= HardTotalBytes, "invalid_bundle_limits");
        Require(limits.MaxEntryBytes is > 0 and <= HardTotalBytes, "invalid_bundle_limits");
        Require(limits.MaxChunkBytes is > 0 and <= HardChunkBytes, "invalid_bundle_limits");
        Require(
            limits.MaxEntries is > 0 and <= HardEntries && limits.MaxCompressionRatio is > 0 and <= 200,
            "invalid_bundle_limits"
        );
    }

    internal static bool IsAllowedPath(string path)
    {
        if (path == "manifest.json" || FixedPaths.Contains(path, StringComparer.Ordinal))
        {
            return true;
        }
        var parts = path.Split('/');
        if (
            parts.Length < 3
            || parts[0] != "captures"
            || !Guid.TryParseExact(parts[1], "D", out var id)
            || id == Guid.Empty
            || parts[1] != id.ToString("D")
        )
        {
            return false;
        }
        return parts.Length == 3 && parts[2] == "manifest.json"
            || parts.Length == 4
                && parts[2] == "chunks"
                && parts[3].Length == 13
                && parts[3].EndsWith(".ndjson", StringComparison.Ordinal)
                && parts[3][..6].All(char.IsAsciiDigit);
    }

    internal static byte[] Ndjson<T>(IReadOnlyList<T> records)
    {
        using var stream = new MemoryStream();
        foreach (var record in records)
        {
            var bytes = AuditCanonicalJson.Serialize(record);
            Require(
                bytes.Length <= ManifestBytes && stream.Length + bytes.Length + 1 <= HardTotalBytes,
                "bundle_size_limit"
            );
            stream.Write(bytes);
            stream.WriteByte(10);
        }
        return stream.ToArray();
    }

    internal static IReadOnlyList<T> ParseNdjson<T>(byte[] data, int recordLimit = 1_000_000)
    {
        var records = new List<T>();
        var start = 0;
        for (var index = 0; index < data.Length; index++)
        {
            if (data[index] != 10)
            {
                continue;
            }
            Require(
                index > start && index - start <= ManifestBytes && records.Count < recordLimit,
                "invalid_bundle_ndjson"
            );
            var line = data.AsSpan(start, index - start);
            var record = AuditCanonicalJson.Deserialize<T>(line);
            Require(line.SequenceEqual(AuditCanonicalJson.Serialize(record)), "noncanonical_bundle_ndjson");
            records.Add(record);
            start = index + 1;
        }
        Require(start == data.Length, "invalid_bundle_ndjson");
        return records;
    }

    internal static AuditInput ValidateEvidence(AuditBundle bundle, BundleLimits limits)
    {
        Require(bundle.Captures.Count <= HardEntries, "bundle_entry_limit");
        Require(
            bundle.Captures.Sum(c => 1L + c.Chunks.Count) + FixedPaths.Length + 1 <= HardEntries,
            "bundle_entry_limit"
        );
        var input = AuditCanonicalJson.Deserialize<AuditInput>(AuditCanonicalJson.Serialize(bundle.Input));
        var manifests = bundle.Captures.Select(c => c.Manifest).ToArray();
        Require(manifests.Select(c => c.CaptureId).Distinct().Count() == manifests.Length, "duplicate_capture");
        Require(
            AuditCanonicalJson
                .Serialize(manifests)
                .AsSpan()
                .SequenceEqual(AuditCanonicalJson.Serialize(input.Captures)),
            "capture_manifest_mismatch"
        );
        var records = new List<SourceUsageRecord>();
        foreach (var capture in bundle.Captures)
        {
            records.AddRange(ValidateCapture(capture, limits));
        }
        var union = input with { Records = records };
        Require(
            AuditCanonicalJson.Serialize(union).AsSpan().SequenceEqual(AuditCanonicalJson.Serialize(input)),
            "capture_record_union_mismatch"
        );
        ValidateResult(input, bundle.Result);
        return input;
    }

    private static IReadOnlyList<SourceUsageRecord> ValidateCapture(AuditCaptureEvidence capture, BundleLimits limits)
    {
        var manifest = capture.Manifest;
        Require(capture.Chunks.Count == manifest.ChunkHashes.Count, "capture_chunk_count_mismatch");
        var records = new List<SourceUsageRecord>();
        long bytes = 0;
        for (var index = 0; index < capture.Chunks.Count; index++)
        {
            var chunk = capture.Chunks[index];
            Require(chunk.Index == index && chunk.Ndjson.Length <= limits.MaxChunkBytes, "invalid_capture_chunk");
            Require(
                AuditCanonicalJson.ContentHash(chunk.Ndjson) == manifest.ChunkHashes[index],
                "capture_chunk_hash_mismatch"
            );
            bytes += chunk.Ndjson.Length;
            Require(bytes <= limits.MaxTotalBytes, "bundle_size_limit");
            records.AddRange(ParseNdjson<SourceUsageRecord>(chunk.Ndjson, 1_000_000 - records.Count));
        }
        Require(records.Count == manifest.RecordCount, "capture_record_count_mismatch");
        Require(records.All(r => IsCaptureMember(manifest, r)), "capture_record_scope_mismatch");
        return records;
    }

    private static bool IsCaptureMember(CaptureManifest manifest, SourceUsageRecord record) =>
        record.MachineId == manifest.MachineId
        && record.Generation == manifest.Generation
        && manifest.Scope.Sources.Any(s => s.MachineId == record.MachineId && s.SourceId == record.SourceId)
        && manifest.SourceFormats.Any(f =>
            f.SourceId == record.SourceId && f.Format == record.SourceFormat && f.Version == record.SourceVersion
        );

    private static void ValidateResult(AuditInput input, AuditResult result)
    {
        _ = AuditCanonicalJson.Serialize(result);
        Require(
            result.InputContentDigest == AuditComputationInput.ContentDigest(input)
                && result.VerifierVersion == input.VerifierVersion
                && result.RuleVersion == input.RuleVersion
                && AuditCanonicalJson
                    .Serialize(result.Scope)
                    .AsSpan()
                    .SequenceEqual(AuditCanonicalJson.Serialize(AuditResultScope.FromScope(input.Scope))),
            "recorded_result_binding_mismatch"
        );
        if (
            input.VerifierVersion == AuditVerifierIdentity.VerifierVersion
            && input.RuleVersion == AuditVerifierIdentity.RuleVersion
        )
        {
            Require(
                AuditCanonicalJson
                    .Serialize(new AuditVerifier().Verify(input))
                    .AsSpan()
                    .SequenceEqual(AuditCanonicalJson.Serialize(result)),
                "recorded_result_mismatch"
            );
        }
    }
}

internal sealed record BundleOptions(
    int SchemaVersion,
    string VerifierVersion,
    string RuleVersion,
    AuditScope Scope,
    AuditOptions Options,
    IReadOnlyList<NamedTolerance> Tolerances,
    FrozenPresentationProbe? PresentationProbe
);
