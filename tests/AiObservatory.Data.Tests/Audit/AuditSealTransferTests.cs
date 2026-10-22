using System.Data.Common;
using AiObservatory.Audit.Serialization;
using AiObservatory.Data.Audit;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NodaTime;

namespace AiObservatory.Data.Tests.Audit;

public sealed partial class AuditStoreTests
{
    [Theory]
    [InlineData("renew")]
    [InlineData("revoke")]
    [InlineData("cancel")]
    [InlineData("append")]
    [InlineData("replace")]
    [InlineData("caller")]
    public async Task CodeRabbit8e_SealByteTransferDoesNotHoldEvidenceFenceAndRejectsChangedCandidate(string action)
    {
        var machine = await Machine();
        var run = await _store.CreateRunAsync(Scope(machine.MachineId), [], "transfer", true, Now, Ct);
        var lease = (await _store.TryClaimCaptureAsync(machine.MachineId, machine.CredentialId, Now, Ct))!;
        var bytes = Record(lease.MachineId);
        var hash = AuditCanonicalJson.ContentHash(bytes);
        await _store.PutChunkAsync(lease, 0, bytes, hash, Ct);
        await Run();
        var runLease = (await _store.TryLeaseRunAsync(Guid.NewGuid(), Now, Ct))!;
        var gate = new GatedCandidateRead();
        await using var reader = new AiObservatoryDbContext(
            new DbContextOptionsBuilder<AiObservatoryDbContext>(_options).AddInterceptors(gate).Options
        );
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var seal = new AuditStore(reader, _clock).SealCaptureAsync(
            lease,
            Manifest(lease, [hash], 1),
            "seal",
            cancellation.Token
        );
        await gate.Entered.Task.WaitAsync(Ct);
        try
        {
            gate.HasTransaction.Should()
                .BeFalse("the full-byte transfer must start after the evidence transaction is released");
            await using var writerDb = new AiObservatoryDbContext(_options);
            var writer = new AuditStore(writerDb, _clock);
            using var progress = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            progress.CancelAfter(TimeSpan.FromSeconds(2));
            (await writer.RenewRunLeaseAsync(runLease, Now, progress.Token)).Should().BeTrue();
            switch (action)
            {
                case "revoke":
                    await writer.RevokeCredentialAsync(lease.CredentialId, progress.Token);
                    break;
                case "cancel":
                    var view = (await writer.GetRunAsync(run, progress.Token))!;
                    await writer.CancelRunAsync(run, view.Revision, progress.Token);
                    break;
                case "append":
                    await writer.PutChunkAsync(lease, 1, bytes, hash, progress.Token);
                    break;
                case "replace":
                    _clock.Advance(Duration.FromMinutes(6));
                    var replacement = (
                        await writer.TryClaimCaptureAsync(
                            machine.MachineId,
                            machine.CredentialId,
                            _clock.GetCurrentInstant(),
                            progress.Token
                        )
                    )!;
                    var larger = bytes.Concat(bytes).ToArray();
                    await writer.PutChunkAsync(
                        replacement,
                        0,
                        larger,
                        AuditCanonicalJson.ContentHash(larger),
                        progress.Token
                    );
                    break;
                case "caller":
                    await cancellation.CancelAsync();
                    break;
            }
            gate.Release.TrySetResult();
            var observe = () => seal;
            if (action == "renew")
            {
                (await seal).Should().Be(lease.RequestId);
            }
            else if (action == "caller")
            {
                await observe.Should().ThrowAsync<OperationCanceledException>();
            }
            else
            {
                (await observe.Should().ThrowAsync<AuditStoreException>())
                    .Which.Code.Should()
                    .Be(action == "revoke" ? "credential_revoked" : "capture_generation_changed");
                if (action != "revoke")
                {
                    gate.ReturnedRows.Should()
                        .BeFalse("a changed request revision must prevent downloading replacement or appended bytes");
                }
            }
            var next = await UploadedCapture();
            (await writer.SealCaptureAsync(next.Lease, next.Manifest, "seal", Ct)).Should().Be(next.Lease.RequestId);
        }
        finally
        {
            await cancellation.CancelAsync();
            gate.Release.TrySetResult();
            await ObserveSeal(seal);
        }
    }

    private sealed class GatedCandidateRead : DbCommandInterceptor
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool HasTransaction { get; private set; }
        internal bool? ReturnedRows { get; private set; }

        private static bool FullBytes(DbCommand command) =>
            command.CommandText.Contains("\"AuditEvidenceChunks\"", StringComparison.Ordinal)
            && command.CommandText.Contains("\"Bytes\"", StringComparison.Ordinal)
            && !command.CommandText.Contains("length(", StringComparison.OrdinalIgnoreCase);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default
        )
        {
            if (FullBytes(command))
            {
                HasTransaction = command.Transaction != null;
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default
        )
        {
            if (FullBytes(command))
            {
                ReturnedRows = result.HasRows;
            }
            return ValueTask.FromResult(result);
        }
    }
}
