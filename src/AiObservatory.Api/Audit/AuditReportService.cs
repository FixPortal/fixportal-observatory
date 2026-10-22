using AiObservatory.Audit.Bundles;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using AiObservatory.Data.Audit;

namespace AiObservatory.Api.Audit;

public sealed class AuditReportService(AuditStore store)
{
    public async Task WriteBundleAsync(Guid runId, Stream destination, CancellationToken ct)
    {
        var materialized = await store.MaterializeExportAsync(runId, ct);
        var bundle = Validate(materialized);
        await AuditBundleWriter.WriteAsync(destination, bundle, ct);
    }

    public async Task<AuditEvidenceView> EvidenceAsync(Guid runId, CancellationToken ct)
    {
        var retained = await store.MaterializeExportAsync(runId, ct);
        var bundle = Validate(retained);
        return new(
            bundle,
            retained.Snapshot.Sha256,
            AiObservatory.Audit.Checks.PresentationTotalsCheck.SummaryInputDigest(bundle.Input)
        );
    }

    private static AuditBundle Validate(AuditExportMaterialization materialized)
    {
        try
        {
            return ValidateDetached(materialized);
        }
        catch (AuditValidationException)
        {
            throw new AuditStoreException("retained_evidence_integrity", 409);
        }
    }

    private static AuditBundle ValidateDetached(AuditExportMaterialization materialized)
    {
        var snapshot = materialized.Snapshot;
        Require(
            AuditCanonicalJson.ContentHash(snapshot.Bytes) == snapshot.Sha256
                && AuditCanonicalJson.ContentHash(materialized.Result) == materialized.ResultHash
        );
        var input = AuditCanonicalJson.Deserialize<AuditInput>(snapshot.Bytes);
        var result = AuditCanonicalJson.Deserialize<AuditResult>(materialized.Result);
        Require(
            AuditComputationInput.ContentDigest(input) == snapshot.InputContentDigest
                && result.InputContentDigest == snapshot.InputContentDigest
        );
        var captures = materialized
            .Captures.Select(c => new AuditCaptureEvidence(
                AuditCanonicalJson.Deserialize<CaptureManifest>(c.Manifest),
                c.Chunks.Select((bytes, index) => new AuditCaptureChunk(index, bytes)).ToArray()
            ))
            .ToArray();
        var bundle = new AuditBundle(input, result, captures);
        AuditBundleWriter.Validate(bundle);
        return bundle;
    }

    private static void Require(bool valid)
    {
        if (!valid)
        {
            throw new AuditStoreException("retained_evidence_integrity", 409);
        }
    }
}

public sealed record AuditEvidenceView(AuditBundle Bundle, string FrozenSnapshotHash, string SummaryInputDigest);
