using AiObservatory.Audit.Bundles;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using AiObservatory.Data.Audit;

namespace AiObservatory.Api.Audit;

public sealed class AuditReportService(AuditStore store, IServiceScopeFactory scopes, NodaTime.IClock clock)
{
    public async Task WriteBundleAsync(Guid runId, Stream destination, CancellationToken ct)
    {
        await using var session = await OpenAsync(runId, ct);
        await using var zip = await PrepareBundleAsync(session);
        await zip.CopyToAsync(destination, session.Token);
    }

    public static async Task<FileStream> PrepareBundleAsync(AuditExportSession session)
    {
        var bundle = Validate(session.Materialized);
        session.Token.ThrowIfCancellationRequested();
        var file = AuditTemporaryBundleStream.Create();
        try
        {
            session.OwnSpool(file);
            // ZipArchive entry disposal writes small synchronous trailers. A seekable private temporary
            // file preserves Task10's exact ZIP bytes and avoids synchronous HTTP I/O or an extra ZIP-sized RAM buffer.
            await AuditBundleWriter.WriteAsync(file, bundle, session.Token);
            session.Token.ThrowIfCancellationRequested();
            file.Position = 0;
            return file;
        }
        catch (Exception ex)
        {
            await DisposePrivateSpool(file);
            if (ex is IOException or UnauthorizedAccessException)
            {
                throw new AuditStoreException("bundle_spool_unavailable", 503);
            }
            throw;
        }
    }

    private static async Task DisposePrivateSpool(FileStream file)
    {
        try
        {
            await file.DisposeAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AuditStoreException("bundle_spool_unavailable", 503);
        }
    }

    public Task<AuditExportSession> OpenAsync(Guid runId, CancellationToken ct) =>
        AuditExportSession.OpenAsync(store, scopes, clock, runId, ct);

    public static AuditEvidenceView Evidence(AuditExportMaterialization retained)
    {
        var bundle = Validate(retained);
        return new(
            bundle,
            retained.Snapshot.Sha256,
            AiObservatory.Audit.Checks.PresentationTotalsCheck.SummaryInputDigest(bundle.Input)
        );
    }

    public static AuditBundle Validate(AuditExportMaterialization materialized)
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
