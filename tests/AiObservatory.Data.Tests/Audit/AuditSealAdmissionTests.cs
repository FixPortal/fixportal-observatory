using System.Data.Common;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using AiObservatory.Data.Audit;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AiObservatory.Data.Tests.Audit;

public sealed partial class AuditStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task R1_WaitingSealDoesNotReadCandidateBytes(bool sameCapture)
    {
        var first = await UploadedCapture();
        var second = sameCapture ? first : await UploadedCapture();
        var gate = new GatedCaptureValidator();
        var reads = new CandidateReads();
        await using var admittedDb = ObservedContext(reads);
        await using var waitingDb = ObservedContext(reads);
        await using var anotherWaitingDb = ObservedContext(reads);
        using var admittedCancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var anotherCancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var admitted = new AuditStore(admittedDb, _clock, captureValidator: gate).SealCaptureAsync(
            first.Lease,
            first.Manifest,
            "seal",
            admittedCancellation.Token
        );
        await gate.Entered.Task.WaitAsync(Ct);
        Task<Guid>? waiting = null;
        Task<Guid>? anotherWaiting = null;
        try
        {
            reads.FullByteQueries.Should().Be(1);
            // The observation budget cancels only the contender. The first validator stays gated
            // until cleanup, so any second candidate read is an admission failure, not a timing race
            // with a legitimately completed first request.
            using var waitingCancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            waitingCancellation.CancelAfter(TimeSpan.FromSeconds(1));
            waiting = new AuditStore(waitingDb, _clock, instanceId: "other-instance").SealCaptureAsync(
                second.Lease,
                second.Manifest,
                "seal",
                waitingCancellation.Token
            );
            anotherWaiting = new AuditStore(anotherWaitingDb, _clock).SealCaptureAsync(
                second.Lease,
                second.Manifest,
                "seal",
                anotherCancellation.Token
            );
            await ObserveSeal(waiting);
            reads.FullByteQueries.Should().Be(1, "waiting seal callers must not allocate another full candidate");
            var observe = () => waiting;
            await observe.Should().ThrowAsync<OperationCanceledException>();
            anotherWaiting.IsCompleted.Should().BeFalse("canceling a waiter must not release the active permit");
            await anotherCancellation.CancelAsync();
            var anotherObserve = () => anotherWaiting;
            await anotherObserve.Should().ThrowAsync<OperationCanceledException>();
            reads.FullByteQueries.Should().Be(1);
        }
        finally
        {
            await admittedCancellation.CancelAsync();
            await anotherCancellation.CancelAsync();
            gate.Release.TrySetResult();
            await ObserveSeal(admitted);
            if (waiting != null)
            {
                await ObserveSeal(waiting);
            }
            if (anotherWaiting != null)
            {
                await ObserveSeal(anotherWaiting);
            }
        }
        await using var retryDb = ObservedContext(reads);
        var retry = new AuditStore(retryDb, _clock);
        var id = await retry.SealCaptureAsync(first.Lease, first.Manifest, "seal", Ct);
        (await retry.SealCaptureAsync(first.Lease, first.Manifest, "seal", Ct)).Should().Be(id);
        reads.FullByteQueries.Should().Be(2, "the idempotent retry must not read the sealed bytes again");
    }

    [Theory]
    [InlineData("revoke", "credential_revoked")]
    [InlineData("expiry", "capture_lease_expired")]
    public async Task R1_QueuedSealRechecksAuthorityBeforeReadingBytes(string action, string expectedCode)
    {
        var first = await UploadedCapture();
        var queued = await UploadedCapture();
        await Run();
        var runLease = (await _store.TryLeaseRunAsync(Guid.NewGuid(), Now, Ct))!;
        var reads = new CandidateReads();
        var gate = new GatedCaptureValidator();
        await using var admittedDb = ObservedContext(reads);
        await using var queuedDb = ObservedContext(reads);
        using var admittedCancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var admitted = new AuditStore(admittedDb, _clock, captureValidator: gate).SealCaptureAsync(
            first.Lease,
            first.Manifest,
            "seal",
            admittedCancellation.Token
        );
        await gate.Entered.Task.WaitAsync(Ct);
        var waiting = new AuditStore(queuedDb, _clock).SealCaptureAsync(queued.Lease, queued.Manifest, "seal", Ct);
        try
        {
            // A distinct scoped context must obtain the evidence fence while validation/admission wait.
            await using var writerDb = new AiObservatoryDbContext(_options);
            var writer = new AuditStore(writerDb, _clock);
            using var progress = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            progress.CancelAfter(TimeSpan.FromSeconds(2));
            (await writer.RenewRunLeaseAsync(runLease, Now, progress.Token)).Should().BeTrue();
            if (action == "revoke")
            {
                await writer.RevokeCredentialAsync(queued.Lease.CredentialId, progress.Token);
            }
            else
            {
                _clock.Advance(NodaTime.Duration.FromMinutes(6));
            }
            await admittedCancellation.CancelAsync();
            var canceled = () => admitted;
            await canceled.Should().ThrowAsync<OperationCanceledException>();
            var rejected = () => waiting;
            (await rejected.Should().ThrowAsync<AuditStoreException>()).Which.Code.Should().Be(expectedCode);
            reads
                .FullByteQueries.Should()
                .Be(1, "queued authority must be read afresh before candidate materialization");
        }
        finally
        {
            await admittedCancellation.CancelAsync();
            gate.Release.TrySetResult();
            await ObserveSeal(admitted);
            await ObserveSeal(waiting);
        }
        // A rejected queued request also releases admission for an unrelated valid capture.
        var next = await UploadedCapture();
        await using var nextDb = ObservedContext(reads);
        (await new AuditStore(nextDb, _clock).SealCaptureAsync(next.Lease, next.Manifest, "seal", Ct))
            .Should()
            .Be(next.Lease.RequestId);
        reads.FullByteQueries.Should().Be(2);
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("validation")]
    [InlineData("existing")]
    [InlineData("conflict")]
    public async Task R1_SealAdmissionRecoversAfterFaultAndEarlyReturn(string action)
    {
        var capture = await UploadedCapture();
        await using var firstDb = new AiObservatoryDbContext(_options);
        var first = new AuditStore(
            firstDb,
            _clock,
            captureValidator: action == "validation" ? new FaultingCaptureValidator() : null
        );
        if (action == "manifest")
        {
            var malformed = () => first.SealCaptureAsync(capture.Lease, "{"u8.ToArray(), "seal", Ct);
            (await malformed.Should().ThrowAsync<AuditValidationException>())
                .Which.Code.Should()
                .Be("invalid_json_contract");
        }
        else if (action == "validation")
        {
            var fault = () => first.SealCaptureAsync(capture.Lease, capture.Manifest, "seal", Ct);
            (await fault.Should().ThrowAsync<InvalidOperationException>())
                .Which.Message.Should()
                .Be("validation fault");
        }
        else
        {
            var id = await first.SealCaptureAsync(capture.Lease, capture.Manifest, "seal", Ct);
            if (action == "existing")
            {
                (await first.SealCaptureAsync(capture.Lease, capture.Manifest, "seal", Ct)).Should().Be(id);
            }
            else
            {
                var conflict = () => first.SealCaptureAsync(capture.Lease, capture.Manifest, "other-key", Ct);
                (await conflict.Should().ThrowAsync<AuditStoreException>())
                    .Which.Code.Should()
                    .Be("seal_retry_conflict");
            }
        }
        var next = await UploadedCapture();
        await using var nextDb = new AiObservatoryDbContext(_options);
        using var progress = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        progress.CancelAfter(TimeSpan.FromSeconds(2));
        (await new AuditStore(nextDb, _clock).SealCaptureAsync(next.Lease, next.Manifest, "seal", progress.Token))
            .Should()
            .Be(next.Lease.RequestId);
    }

    private sealed class FaultingCaptureValidator : AuditCaptureValidator
    {
        public override Task ValidateAsync(
            CaptureManifest manifest,
            IReadOnlyList<AuditSealChunk> chunks,
            CancellationToken ct
        ) => throw new InvalidOperationException("validation fault");
    }

    private async Task<(CaptureLease Lease, byte[] Manifest)> UploadedCapture()
    {
        var lease = await Capture();
        var bytes = Record(lease.MachineId);
        var hash = AuditCanonicalJson.ContentHash(bytes);
        await _store.PutChunkAsync(lease, 0, bytes, hash, Ct);
        return (lease, Manifest(lease, [hash], 1));
    }

    private AiObservatoryDbContext ObservedContext(CandidateReads reads) =>
        new(new DbContextOptionsBuilder<AiObservatoryDbContext>(_options).AddInterceptors(reads).Options);

    private static async Task ObserveSeal(Task<Guid> seal)
    {
        try
        {
            await seal;
        }
        catch (Exception ex) when (ex is OperationCanceledException or AuditStoreException)
        {
            // All expected failures are asserted by the test; cleanup only drains owned operations.
        }
    }

    private sealed class CandidateReads : DbCommandInterceptor
    {
        private int _fullByteQueries;
        internal int FullByteQueries => Volatile.Read(ref _fullByteQueries);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default
        )
        {
            if (
                command.CommandText.Contains("\"AuditEvidenceChunks\"", StringComparison.Ordinal)
                && command.CommandText.Contains("\"Bytes\"", StringComparison.Ordinal)
                && !command.CommandText.Contains("length(", StringComparison.OrdinalIgnoreCase)
            )
            {
                Interlocked.Increment(ref _fullByteQueries);
            }
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
