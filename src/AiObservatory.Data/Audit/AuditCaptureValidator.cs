using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;

namespace AiObservatory.Data.Audit;

/// <summary>Validates a detached capture candidate without database access.</summary>
public class AuditCaptureValidator
{
    public virtual Task ValidateAsync(
        CaptureManifest manifest,
        IReadOnlyList<AuditSealChunk> chunks,
        CancellationToken ct
    )
    {
        var identities = new Dictionary<(string Source, string Generation, string File, long Sequence), string>();
        for (var index = 0; index < chunks.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = chunks[index];
            Require(
                chunk.Index == index
                    && chunk.Sha256 == manifest.ChunkHashes[index]
                    && AuditCanonicalJson.ContentHash(chunk.Bytes.Span) == chunk.Sha256,
                "capture_chunk_mismatch"
            );
            foreach (var record in AuditStore.ParseRecords(chunk.Bytes.ToArray(), ct))
            {
                ct.ThrowIfCancellationRequested();
                Require(
                    record.MachineId == manifest.MachineId
                        && record.Generation == manifest.Generation
                        && manifest.Scope.Sources.Any(s =>
                            s.MachineId == record.MachineId && s.SourceId == record.SourceId
                        )
                        && manifest.SourceFormats.Any(f =>
                            f.SourceId == record.SourceId
                            && f.Format == record.SourceFormat
                            && f.Version == record.SourceVersion
                        ),
                    "capture_record_membership"
                );
                var identity = (record.SourceId, record.Generation, record.FileId, record.Sequence);
                var hash = AuditCanonicalJson.ContentHash(AuditCanonicalJson.Serialize(record));
                Require(
                    !identities.TryGetValue(identity, out var prior) || prior == hash,
                    "capture_record_identity_conflict"
                );
                identities[identity] = hash;
            }
        }
        return Task.CompletedTask;
    }

    private static void Require(bool condition, string code)
    {
        if (!condition)
        {
            throw new AuditStoreException(code, 409);
        }
    }
}
