using System.Data.Common;
using AiObservatory.Api.Audit;
using AiObservatory.Audit;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using AiObservatory.Data;
using AiObservatory.Data.Audit;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using NodaTime;

namespace AiObservatory.Api.IntegrationTests;

public sealed partial class AuditWorkerTests
{
    [Fact]
    public async Task SelectedExecution_MismatchedInstanceRefusesBeforeAdmissionOrCoreEntry()
    {
        var seed = new AuditStore(_db, _clock, _audit, "Selected-A");
        var scope = Scope(Guid.NewGuid());
        var id = await seed.CreateRunAsync(scope, [], "selected-instance-mismatch", false, Now, Ct);
        var before = (await seed.GetRunAsync(id, Ct))!;
        var binding = new SelectedRunBinding(id, AuditCanonicalJson.ContentHash(before.Scope), before.Revision);
        var facts = await UnrelatedFacts(Guid.Empty);
        var witness = new SelectedExecutionWitness();
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>(_options).AddInterceptors(witness).Options;
        await using var admissionDb = new AiObservatoryDbContext(options);
        await using var workerDb = new AiObservatoryDbContext(options);
        var admissionStore = new AuditStore(admissionDb, _clock, _audit, "Selected-A");
        var workerStore = new AuditStore(workerDb, _clock, _audit, "selected-a");
        var worker = new AuditWorkerService(workerDb, workerStore, _clock, Options.Create(_audit), options, witness);
        var service = new AuditSelectedRunService(admissionStore, worker, Options.Create(_audit));
        Task Execute() => service.ExecuteAsync(binding, Guid.NewGuid(), Now + Duration.FromHours(1), Ct);

        var failure = await Record.ExceptionAsync(Execute);

        var after = (await seed.GetRunAsync(id, Ct))!;
        after.Attempts.Should().Be(0, "a composition mismatch must not consume fresh selected authority");
        after.Should().BeEquivalentTo(before);
        (await UnrelatedFacts(Guid.Empty)).Should().Be(facts);
        witness.TransactionsStarted.Should().Be(0);
        witness.VerifierCalls.Should().Be(0);
        failure.Should().BeOfType<AuditStoreException>().Which.Code.Should().Be("selected_instance_conflict");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectedExecution_AcknowledgedPartialProceedRefusesFreshBindingWithoutWrites(bool shared)
    {
        var ready = await _store.RegisterMachineAsync(new byte[32], Ct);
        var unfinished = await _store.RegisterMachineAsync(new byte[32], Ct);
        var scope = Scope(ready.MachineId) with
        {
            Sources = [new(ready.MachineId, "claude-local"), new(unfinished.MachineId, "codex-local")],
        };
        var id = await Runs.CreateAsync(new(scope, [], true), "selected-acknowledged-partial", Ct);
        var originalRevision = (await _store.GetRunAsync(id, Ct))!.Revision;
        await Seal((await _store.TryClaimCaptureAsync(ready.MachineId, ready.CredentialId, Now, Ct))!);
        var unfinishedLease = (
            await _store.TryClaimCaptureAsync(unfinished.MachineId, unfinished.CredentialId, Now, Ct)
        )!;
        if (shared)
        {
            var otherScope = scope with { Sources = [new(unfinished.MachineId, "codex-local")] };
            var other = await Runs.CreateAsync(new(otherScope, [], false), "selected-partial-shared-other", Ct);
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"AuditRuns\" SET \"State\"='collecting', \"CaptureRequestIds\"={new[] { unfinishedLease.RequestId }} WHERE \"Id\"={other}",
                Ct
            );
        }
        await _store.ProceedRunAsync(id, Ct);
        if (shared)
        {
            await Seal(unfinishedLease);
        }
        (
            await _db
                .Database.SqlQuery<bool>(
                    $"SELECT \"PartialEvidenceAcknowledged\" AS \"Value\" FROM \"AuditRuns\" WHERE \"Id\"={id}"
                )
                .SingleAsync(Ct)
        )
            .Should()
            .BeTrue();
        (
            await _db
                .Database.SqlQuery<string>(
                    $"SELECT \"State\" AS \"Value\" FROM \"AuditCaptureRequests\" WHERE \"Id\"={unfinishedLease.RequestId}"
                )
                .SingleAsync(Ct)
        )
            .Should()
            .Be(shared ? "sealed" : "expired");
        var binding = await SelectedBinding(id);
        binding.ExpectedRevision.Should().BeGreaterThan(originalRevision);
        var before = (await _store.GetRunAsync(id, Ct))!;
        var facts = await UnrelatedFacts(Guid.Empty);
        Task Execute() => Selected.ExecuteAsync(binding, Guid.NewGuid(), Now + Duration.FromHours(1), Ct);

        var failure = await Record.ExceptionAsync(Execute);

        var after = (await _store.GetRunAsync(id, Ct))!;
        after.Attempts.Should().Be(0, "acknowledgement remains excluded even after a retained shared request seals");
        after.Should().BeEquivalentTo(before);
        (await UnrelatedFacts(Guid.Empty)).Should().Be(facts);
        failure.Should().BeOfType<AuditStoreException>().Which.Code.Should().Be("selected_partial_not_supported");
    }

    [Fact]
    public async Task SelectedExecution_AllowPartialScopeWithoutAcknowledgementRemainsEligible()
    {
        var machine = await _store.RegisterMachineAsync(new byte[32], Ct);
        var scope = Scope(machine.MachineId);
        scope.AllowPartial.Should().BeTrue();
        var id = await Runs.CreateAsync(new(scope, [], true), "selected-unacknowledged-control", Ct);
        await Seal((await _store.TryClaimCaptureAsync(machine.MachineId, machine.CredentialId, Now, Ct))!);
        (
            await _db
                .Database.SqlQuery<bool>(
                    $"SELECT \"PartialEvidenceAcknowledged\" AS \"Value\" FROM \"AuditRuns\" WHERE \"Id\"={id}"
                )
                .SingleAsync(Ct)
        )
            .Should()
            .BeFalse();
        (await Selected.ExecuteAsync(await SelectedBinding(id), Guid.NewGuid(), Now + Duration.FromHours(1), Ct))
            .Should()
            .Be("attempt_finished");
        (await _store.GetRunAsync(id, Ct))!.State.Should().Be("completed");
    }

    [Fact]
    public async Task SelectedExecution_CustomInstanceRenewalPreservesOwnedAttempt()
    {
        _audit.RunLeaseRenewalInterval = TimeSpan.FromMilliseconds(25);
        var scope = Scope(Guid.NewGuid());
        var store = new AuditStore(_db, _clock, _audit, "selected-custom");
        var id = await store.CreateRunAsync(scope, [], "selected-custom-renewal", false, Now, Ct);
        var signal = new SelectedRenewalSignal(id, Now + Duration.FromMinutes(5));
        var renewalOptions = new DbContextOptionsBuilder<AiObservatoryDbContext>(_options)
            .AddInterceptors(signal)
            .Options;
        var gate = new SelectedVerifierGate();
        var workerStore = new AuditStore(_db, _clock, _audit, "selected-custom");
        var worker = new AuditWorkerService(_db, workerStore, _clock, Options.Create(_audit), renewalOptions, gate);
        var service = new AuditSelectedRunService(store, worker, Options.Create(_audit));
        var binding = new SelectedRunBinding(
            id,
            AuditCanonicalJson.ContentHash(AuditCanonicalJson.Serialize(scope)),
            0
        );
        var execution = service.ExecuteAsync(binding, Guid.NewGuid(), Now + Duration.FromHours(1), Ct);
        try
        {
            var entered = await Task.WhenAny(gate.Entered.Task, execution).WaitAsync(Ct);
            entered.Should().BeSameAs(gate.Entered.Task, "custom-instance ownership must survive until verification");
            _clock.Advance(Duration.FromMinutes(4));
            var settled = await Task.WhenAny(signal.Extended.Task, execution).WaitAsync(Ct);
            settled
                .Should()
                .BeSameAs(
                    signal.Extended.Task,
                    "the persisted custom-instance lease must extend before the owned verifier can finish"
                );
        }
        finally
        {
            gate.Release.TrySetResult();
            await execution;
        }
        (await store.GetRunAsync(id, Ct))!.State.Should().Be("completed");
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("run")]
    [InlineData("summary")]
    [InlineData("capture")]
    [InlineData("snapshot")]
    public async Task SelectedExecution_TemporalEligibilityCrossedDuringCapacityReadRefusesWithoutMutation(string fence)
    {
        var machine = await _store.RegisterMachineAsync(new byte[32], Ct);
        var id = await Runs.CreateAsync(
            new(Scope(machine.MachineId), [], fence == "capture"),
            "selected-slow-read",
            Ct
        );
        var expiry = Now + Duration.FromMinutes(1);
        if (fence == "run")
        {
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"AuditRuns\" SET \"EvidenceExpiresAt\"={expiry} WHERE \"Id\"={id}",
                Ct
            );
        }
        else if (fence == "summary")
        {
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"AuditRuns\" SET \"SummaryExpiresAt\"={expiry} WHERE \"Id\"={id}",
                Ct
            );
        }
        else if (fence == "capture")
        {
            var capture = await Seal(
                (await _store.TryClaimCaptureAsync(machine.MachineId, machine.CredentialId, Now, Ct))!
            );
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"AuditEvidenceCaptures\" SET \"ExpiresAt\"={expiry} WHERE \"Id\"={capture}",
                Ct
            );
        }
        else if (fence == "snapshot")
        {
            var lease = (await _store.TryLeaseRunAsync(Guid.NewGuid(), Now, Ct))!;
            await new AuditSnapshotService(_db, _store, lease).CreateAsync(id, Ct);
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"AuditServerSnapshots\" SET \"ExpiresAt\"={expiry} WHERE \"RunId\"={id}",
                Ct
            );
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"AuditRuns\" SET \"State\"='queued', \"Attempts\"=0, \"WorkerId\"=NULL, \"LeaseExpiresAt\"=NULL WHERE \"Id\"={id}",
                Ct
            );
        }
        var binding = await SelectedBinding(id);
        var before = await UnrelatedFacts(Guid.Empty);
        var signal = new SelectedCapacityClockAdvance(_clock);
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>(_options).AddInterceptors(signal).Options;
        await using var db = new AiObservatoryDbContext(options);
        var store = new AuditStore(db, _clock, _audit);
        var action = () =>
            store.TryAdmitSelectedRunAsync(
                binding,
                Guid.NewGuid(),
                fence == "grant" ? expiry : Now + Duration.FromHours(1),
                Ct
            );
        await action.Should().ThrowAsync<AuditStoreException>();
        signal.Advanced.Should().BeTrue();
        (await UnrelatedFacts(Guid.Empty)).Should().Be(before);
    }

    private sealed class SelectedCapacityClockAdvance(NodaTime.Testing.FakeClock clock) : DbCommandInterceptor
    {
        public bool Advanced { get; private set; }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default
        )
        {
            if (
                command.CommandText.Contains("count(*)", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("AuditRuns", StringComparison.Ordinal)
            )
            {
                clock.Advance(Duration.FromMinutes(1));
                Advanced = true;
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class SelectedRenewalSignal(Guid id, Instant originalExpiry) : DbTransactionInterceptor
    {
        public TaskCompletionSource Extended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default
        )
        {
            await using var command = eventData.Context!.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT \"LeaseExpiresAt\">@expiry FROM \"AuditRuns\" WHERE \"Id\"=@id";
            var expiry = command.CreateParameter();
            expiry.ParameterName = "expiry";
            expiry.DbType = System.Data.DbType.DateTime;
            expiry.Value = originalExpiry.ToDateTimeUtc();
            command.Parameters.Add(expiry);
            var run = command.CreateParameter();
            run.ParameterName = "id";
            run.Value = id;
            command.Parameters.Add(run);
            if (await command.ExecuteScalarAsync(cancellationToken) is true)
            {
                Extended.TrySetResult();
            }
        }
    }

    private async Task<SelectedRunBinding> SelectedBinding(Guid id)
    {
        var run = (await _store.GetRunAsync(id, Ct))!;
        return new(id, AuditCanonicalJson.ContentHash(run.Scope), run.Revision);
    }

    private AuditSelectedRunService Selected => new(_store, Worker, Options.Create(_audit));

    [Fact]
    public async Task SelectedExecution_ConcurrentDuplicatePublishesOneAttempt()
    {
        var id = await Runs.CreateAsync(new(Scope(Guid.NewGuid()), [], false), "selected-concurrent", Ct);
        var binding = await SelectedBinding(id);
        await using var other = new AiObservatoryDbContext(_options);
        var otherStore = new AuditStore(other, _clock, _audit);
        var otherWorker = new AuditWorkerService(other, otherStore, _clock, Options.Create(_audit), _options);
        var otherService = new AuditSelectedRunService(otherStore, otherWorker, Options.Create(_audit));
        async Task<string> Execute(AuditSelectedRunService service)
        {
            try
            {
                return await service.ExecuteAsync(binding, Guid.NewGuid(), Now + Duration.FromHours(1), Ct);
            }
            catch (AuditStoreException)
            {
                return "conflict";
            }
        }
        (await Task.WhenAll(Execute(Selected), Execute(otherService)))
            .Should()
            .BeEquivalentTo("attempt_finished", "conflict");
        var run = (await _store.GetRunAsync(id, Ct))!;
        run.State.Should().Be("completed");
        run.Attempts.Should().Be(1);
        (
            await _db
                .Database.SqlQuery<long>(
                    $"SELECT count(*) AS \"Value\" FROM \"AuditServerSnapshots\" WHERE \"RunId\"={id}"
                )
                .SingleAsync(Ct)
        )
            .Should()
            .Be(1);
    }

    [Theory]
    [InlineData("request_scope")]
    [InlineData("capture_machine")]
    [InlineData("capture_expiry")]
    public async Task SelectedExecution_InvalidSelectedCaptureFactsRefuseWithoutMutation(string defect)
    {
        var machine = await _store.RegisterMachineAsync(new byte[32], Ct);
        var id = await Runs.CreateAsync(new(Scope(machine.MachineId), [], true), "selected-invalid-capture", Ct);
        var binding = await SelectedBinding(id);
        var claim = (await _store.TryClaimCaptureAsync(machine.MachineId, machine.CredentialId, Now, Ct))!;
        var capture = await Seal(claim);
        if (defect == "request_scope")
        {
            var scope = AuditCanonicalJson.Serialize(Scope(machine.MachineId) with { AllowPartial = false });
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"AuditCaptureRequests\" SET \"Scope\"={scope} WHERE \"Id\"={claim.RequestId}",
                Ct
            );
        }
        else if (defect == "capture_machine")
        {
            var otherMachine = await _store.RegisterMachineAsync(new byte[32], Ct);
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"AuditEvidenceCaptures\" SET \"MachineId\"={otherMachine.MachineId} WHERE \"Id\"={capture}",
                Ct
            );
        }
        else
        {
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"AuditEvidenceCaptures\" SET \"ExpiresAt\"={Now} WHERE \"Id\"={capture}",
                Ct
            );
        }
        var before = await UnrelatedFacts(Guid.Empty);
        var action = () => Selected.ExecuteAsync(binding, Guid.NewGuid(), Now + Duration.FromHours(1), Ct);
        await action.Should().ThrowAsync<AuditStoreException>();
        (await UnrelatedFacts(Guid.Empty)).Should().Be(before);
    }

    [Fact]
    public async Task SelectedExecution_SealedEvidenceRemainsReadyAfterUploadDeadline()
    {
        var machine = await _store.RegisterMachineAsync(new byte[32], Ct);
        var id = await Runs.CreateAsync(new(Scope(machine.MachineId), [], true), "selected-sealed-deadline", Ct);
        var claim = (await _store.TryClaimCaptureAsync(machine.MachineId, machine.CredentialId, Now, Ct))!;
        await Seal(claim);
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"AuditCaptureRequests\" SET \"Deadline\"={Now} WHERE \"Id\"={claim.RequestId}",
            Ct
        );
        (await Selected.ExecuteAsync(await SelectedBinding(id), Guid.NewGuid(), Now + Duration.FromHours(1), Ct))
            .Should()
            .Be("attempt_finished");
        (await _store.GetRunAsync(id, Ct))!.State.Should().Be("completed");
    }

    [Fact]
    public async Task SelectedExecution_UnsealedExpiredRequestRefusesWithoutMutation()
    {
        var machine = await _store.RegisterMachineAsync(new byte[32], Ct);
        var id = await Runs.CreateAsync(new(Scope(machine.MachineId), [], true), "selected-unsealed-deadline", Ct);
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"AuditCaptureRequests\" SET \"Deadline\"={Now} WHERE \"Id\" IN (SELECT unnest(\"CaptureRequestIds\") FROM \"AuditRuns\" WHERE \"Id\"={id})",
            Ct
        );
        var before = await UnrelatedFacts(Guid.Empty);
        var binding = await SelectedBinding(id);
        var action = () => Selected.ExecuteAsync(binding, Guid.NewGuid(), Now + Duration.FromHours(1), Ct);
        await action.Should().ThrowAsync<AuditStoreException>();
        (await UnrelatedFacts(Guid.Empty)).Should().Be(before);
    }

    [Fact]
    public async Task SelectedExecution_BusyReadyCollectionDoesNotLinkOrPersistRetentionDefault()
    {
        await Runs.CreateAsync(new(Scope(Guid.NewGuid()), [], false), "selected-busy-live", Ct);
        await _store.TryLeaseRunAsync(Guid.NewGuid(), Now, Ct);
        var machine = await _store.RegisterMachineAsync(new byte[32], Ct);
        var id = await Runs.CreateAsync(new(Scope(machine.MachineId), [], true), "selected-busy-ready", Ct);
        await Seal((await _store.TryClaimCaptureAsync(machine.MachineId, machine.CredentialId, Now, Ct))!);
        await _db.Database.ExecuteSqlRawAsync("DELETE FROM \"AuditRetentionSettings\"", Ct);
        var before = await UnrelatedFacts(Guid.Empty);
        (await Selected.ExecuteAsync(await SelectedBinding(id), Guid.NewGuid(), Now + Duration.FromHours(1), Ct))
            .Should()
            .Be("busy");
        (await UnrelatedFacts(Guid.Empty)).Should().Be(before);
        (
            await _db
                .Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM \"AuditRetentionSettings\"")
                .SingleAsync(Ct)
        )
            .Should()
            .Be(0);
    }

    [Fact]
    public async Task SelectedExecution_CallerCancellationAwaitsOwnedAttemptWithoutPublication()
    {
        var id = await Runs.CreateAsync(new(Scope(Guid.NewGuid()), [], false), "selected-caller-cancel", Ct);
        var gate = new SelectedVerifierGate();
        var worker = new AuditWorkerService(_db, _store, _clock, Options.Create(_audit), _options, gate);
        var service = new AuditSelectedRunService(_store, worker, Options.Create(_audit));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var execution = service.ExecuteAsync(
            await SelectedBinding(id),
            Guid.NewGuid(),
            Now + Duration.FromHours(1),
            caller.Token
        );
        await gate.Entered.Task.WaitAsync(Ct);
        await caller.CancelAsync();
        (await execution.WaitAsync(Ct)).Should().Be("attempt_finished");
        var run = (await _store.GetRunAsync(id, Ct))!;
        run.State.Should().Be("executing");
        run.Result.Should().BeNull();
        gate.Release.TrySetResult();
    }

    [Fact]
    public async Task SelectedExecution_OlderStateAndEvidenceMatrixRemainsByteForByteUnchanged()
    {
        var expiredExecuting = await Runs.CreateAsync(
            new(Scope(Guid.NewGuid()), [], false),
            "selected-matrix-exhausted",
            Ct
        );
        var lease = (await _store.TryLeaseRunAsync(Guid.NewGuid(), Now, Ct))!;
        await new AuditSnapshotService(_db, _store, lease).CreateAsync(expiredExecuting, Ct);
        var frozen = (await _store.ReadSnapshotAsync(expiredExecuting, Ct))!;
        var incompatible = AuditCanonicalJson.Deserialize<AuditInput>(frozen.Bytes) with { VerifierVersion = "1.0.0" };
        var incompatibleBytes = AuditCanonicalJson.Serialize(incompatible);
        var incompatibleHash = AuditCanonicalJson.ContentHash(incompatibleBytes);
        var incompatibleDigest = AuditComputationInput.ContentDigest(incompatible);
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"AuditServerSnapshots\" SET \"Bytes\"={incompatibleBytes}, \"Sha256\"={incompatibleHash}, \"InputContentDigest\"={incompatibleDigest} WHERE \"RunId\"={expiredExecuting}",
            Ct
        );
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"AuditRuns\" SET \"Attempts\"=3, \"LeaseExpiresAt\"={Now - Duration.FromMinutes(1)} WHERE \"Id\"={expiredExecuting}",
            Ct
        );
        var machine = await _store.RegisterMachineAsync(new byte[32], Ct);
        var collecting = await Runs.CreateAsync(
            new(Scope(machine.MachineId), [], true),
            "selected-matrix-collecting",
            Ct
        );
        var capture = await Seal(
            (await _store.TryClaimCaptureAsync(machine.MachineId, machine.CredentialId, Now, Ct))!
        );
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"AuditEvidenceCaptures\" SET \"ExpiresAt\"={Now} WHERE \"Id\"={capture}",
            Ct
        );
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"AuditCaptureRequests\" SET \"Deadline\"={Now} WHERE \"Id\" IN (SELECT unnest(\"CaptureRequestIds\") FROM \"AuditRuns\" WHERE \"Id\"={collecting})",
            Ct
        );
        await Runs.CreateAsync(new(Scope(Guid.NewGuid()), [], false), "selected-matrix-older-queued", Ct);
        var selected = await Runs.CreateAsync(new(Scope(Guid.NewGuid()), [], false), "selected-matrix-target", Ct);
        var before = await UnrelatedFacts(selected);
        await Selected.ExecuteAsync(await SelectedBinding(selected), Guid.NewGuid(), Now + Duration.FromHours(1), Ct);
        (await UnrelatedFacts(selected)).Should().Be(before);
        (await _store.GetRunAsync(selected, Ct))!.State.Should().Be("completed");
    }

    private Task<string> UnrelatedFacts(Guid selected) =>
        _db
            .Database.SqlQuery<string>(
                $"""
                SELECT jsonb_build_object(
                  'runs',(SELECT jsonb_agg(to_jsonb(r) ORDER BY r."Id") FROM "AuditRuns" r WHERE r."Id"<>{selected}),
                  'snapshots',(SELECT jsonb_agg(to_jsonb(s) ORDER BY s."Id") FROM "AuditServerSnapshots" s WHERE s."RunId"<>{selected}),
                  'links',(SELECT jsonb_agg(to_jsonb(l) ORDER BY l."RunId",l."CaptureId") FROM "AuditRunCaptures" l WHERE l."RunId"<>{selected}),
                  'captures',(SELECT jsonb_agg(to_jsonb(c) ORDER BY c."Id") FROM "AuditEvidenceCaptures" c),
                  'chunks',(SELECT jsonb_agg(to_jsonb(c) ORDER BY c."RequestId",c."Index") FROM "AuditEvidenceChunks" c),
                  'requests',(SELECT jsonb_agg(to_jsonb(r) ORDER BY r."Id") FROM "AuditCaptureRequests" r)
                )::text AS "Value"
                """
            )
            .SingleAsync(Ct);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task SelectedExecution_RequiresAuditOnHostedWorkerOff(bool enabled, bool worker)
    {
        var id = await Runs.CreateAsync(new(Scope(Guid.NewGuid()), [], false), "selected-disabled", Ct);
        var binding = await SelectedBinding(id);
        _audit.Enabled = enabled;
        _audit.WorkerEnabled = worker;
        var action = () => Selected.ExecuteAsync(binding, Guid.NewGuid(), Now + Duration.FromHours(1), Ct);
        await action.Should().ThrowAsync<AuditStoreException>();
        (await _store.GetRunAsync(id, Ct))!.Attempts.Should().Be(0);
        (await _store.ReadSnapshotAsync(id, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task SelectedExecution_ReadyCaptureLinksAndConsumesOneRevision()
    {
        var machine = await _store.RegisterMachineAsync(new byte[32], Ct);
        var id = await Runs.CreateAsync(new(Scope(machine.MachineId), [], true), "selected-ready", Ct);
        var binding = await SelectedBinding(id);
        var capture = await Seal(
            (await _store.TryClaimCaptureAsync(machine.MachineId, machine.CredentialId, Now, Ct))!
        );
        var admission = await _store.TryAdmitSelectedRunAsync(binding, Guid.NewGuid(), Now + Duration.FromHours(1), Ct);
        admission.Lease!.Revision.Should().Be(binding.ExpectedRevision + 1);
        var materialization = await _store.ReadSnapshotMaterializationAsync(admission.Lease, Ct);
        materialization.Captures.Should().ContainSingle();
        AuditCanonicalJson
            .Deserialize<CaptureManifest>(materialization.Captures[0].Manifest)
            .CaptureId.Should()
            .Be(capture);
        (await _store.GetRunAsync(id, Ct))!.Attempts.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectedExecution_ReusesPinnedInputAndOnlySelectedCompatibilityMayFail(bool incompatible)
    {
        var id = await Runs.CreateAsync(new(Scope(Guid.NewGuid()), [], false), "selected-pinned", Ct);
        var prior = (await _store.TryLeaseRunAsync(Guid.NewGuid(), Now, Ct))!;
        await new AuditSnapshotService(_db, _store, prior).CreateAsync(id, Ct);
        if (incompatible)
        {
            var pinned = (await _store.ReadSnapshotAsync(id, Ct))!;
            var input = AuditCanonicalJson.Deserialize<AuditInput>(pinned.Bytes) with { VerifierVersion = "1.0.0" };
            var bytes = AuditCanonicalJson.Serialize(input);
            var hash = AuditCanonicalJson.ContentHash(bytes);
            var digest = AuditComputationInput.ContentDigest(input);
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"AuditServerSnapshots\" SET \"Bytes\"={bytes}, \"Sha256\"={hash}, \"InputContentDigest\"={digest} WHERE \"RunId\"={id}",
                Ct
            );
        }
        // Craft a fresh selected row with retained evidence; this is not resume authority.
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"AuditRuns\" SET \"State\"='queued', \"Attempts\"=0, \"WorkerId\"=NULL, \"LeaseExpiresAt\"=NULL WHERE \"Id\"={id}",
            Ct
        );
        var frozen = (await _store.ReadSnapshotAsync(id, Ct))!;
        var unrelated = await Runs.CreateAsync(new(Scope(Guid.NewGuid()), [], false), "selected-pinned-other", Ct);
        var before = (await _store.GetRunAsync(unrelated, Ct))!;
        (await Selected.ExecuteAsync(await SelectedBinding(id), Guid.NewGuid(), Now + Duration.FromHours(1), Ct))
            .Should()
            .Be("attempt_finished");
        (await _store.ReadSnapshotAsync(id, Ct))!.Bytes.Should().Equal(frozen.Bytes);
        var result = (await _store.GetRunAsync(id, Ct))!;
        result.State.Should().Be(incompatible ? "failed" : "completed");
        if (incompatible)
        {
            result.DiagnosticCode.Should().Be("incompatible_audit_verifier");
        }
        (await _store.GetRunAsync(unrelated, Ct)).Should().BeEquivalentTo(before);
    }

    [Fact]
    public Task SelectedExecution_BeforeAdmissionCommitFailureRollsBackWithoutCoreEntry() =>
        AssertSelectedAdmissionCommitFailure(committed: false);

    [Fact]
    public Task SelectedExecution_UncertainAdmissionCommitNeverStartsCore() =>
        AssertSelectedAdmissionCommitFailure(committed: true);

    private async Task AssertSelectedAdmissionCommitFailure(bool committed)
    {
        var machine = await _store.RegisterMachineAsync(new byte[32], Ct);
        var id = await Runs.CreateAsync(new(Scope(machine.MachineId), [], true), "selected-commit", Ct);
        var binding = await SelectedBinding(id);
        await Seal((await _store.TryClaimCaptureAsync(machine.MachineId, machine.CredentialId, Now, Ct))!);
        var fault = new SelectedCommitFailure(committed);
        var witness = new SelectedExecutionWitness();
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>(_options)
            .AddInterceptors(fault, witness)
            .Options;
        await using var db = new AiObservatoryDbContext(options);
        var store = new AuditStore(db, _clock, _audit);
        var worker = new AuditWorkerService(db, store, _clock, Options.Create(_audit), options, witness);
        var service = new AuditSelectedRunService(store, worker, Options.Create(_audit));
        Task Execute() => service.ExecuteAsync(binding, Guid.NewGuid(), Now + Duration.FromHours(1), Ct);
        var failure = await Record.ExceptionAsync(Execute);
        fault.FaultsTriggered.Should().Be(1);
        witness
            .TransactionsStarted.Should()
            .Be(
                1,
                "only admission may start a transaction; entering the owned core after uncertain admission is forbidden"
            );
        witness.VerifierCalls.Should().Be(0);
        failure.Should().BeOfType<InvalidOperationException>();
        var run = (await _store.GetRunAsync(id, Ct))!;
        run.Attempts.Should().Be(committed ? 1 : 0);
        run.Revision.Should().Be(binding.ExpectedRevision + (committed ? 1 : 0));
        run.State.Should().Be(committed ? "executing" : "collecting");
        (
            await _db
                .Database.SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM \"AuditRunCaptures\" WHERE \"RunId\"={id}")
                .SingleAsync(Ct)
        )
            .Should()
            .Be(committed ? 1 : 0);
        run.Result.Should().BeNull();
        (await _store.ReadSnapshotAsync(id, Ct)).Should().BeNull("the core must not start after either commit failure");
    }

    [Fact]
    public async Task SelectedExecution_AdmissionCommitWitnessControlReachesCoreAndVerifier()
    {
        var machine = await _store.RegisterMachineAsync(new byte[32], Ct);
        var id = await Runs.CreateAsync(new(Scope(machine.MachineId), [], true), "selected-commit-control", Ct);
        var binding = await SelectedBinding(id);
        await Seal((await _store.TryClaimCaptureAsync(machine.MachineId, machine.CredentialId, Now, Ct))!);
        var fault = new SelectedCommitFailure(committed: true, enabled: false);
        var witness = new SelectedExecutionWitness();
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>(_options)
            .AddInterceptors(fault, witness)
            .Options;
        await using var db = new AiObservatoryDbContext(options);
        var store = new AuditStore(db, _clock, _audit);
        var worker = new AuditWorkerService(db, store, _clock, Options.Create(_audit), options, witness);
        var service = new AuditSelectedRunService(store, worker, Options.Create(_audit));

        (await service.ExecuteAsync(binding, Guid.NewGuid(), Now + Duration.FromHours(1), Ct))
            .Should()
            .Be("attempt_finished");

        fault.FaultsTriggered.Should().Be(0);
        witness.TransactionsStarted.Should().BeGreaterThan(1);
        witness.VerifierCalls.Should().Be(1);
        var run = (await _store.GetRunAsync(id, Ct))!;
        run.State.Should().Be("completed");
        run.Result.Should().NotBeNull();
        (await _store.ReadSnapshotAsync(id, Ct)).Should().NotBeNull();
    }

    [Fact]
    public async Task SelectedExecution_ExpiryIsRecheckedAfterEvidenceLockWait()
    {
        var id = await Runs.CreateAsync(new(Scope(Guid.NewGuid()), [], false), "selected-lock-expiry", Ct);
        var binding = await SelectedBinding(id);
        await using var locker = new AiObservatoryDbContext(_options);
        await using var transaction = await locker.Database.BeginTransactionAsync(Ct);
        await locker.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({AuditStore.EvidenceLockKey})",
            Ct
        );
        var gate = new SelectedLockSignal();
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>(_options).AddInterceptors(gate).Options;
        await using var waiting = new AiObservatoryDbContext(options);
        var store = new AuditStore(waiting, _clock, _audit);
        var admission = store.TryAdmitSelectedRunAsync(binding, Guid.NewGuid(), Now + Duration.FromMinutes(1), Ct);
        try
        {
            await gate.Entered.Task.WaitAsync(Ct);
            _clock.Advance(Duration.FromMinutes(1));
        }
        finally
        {
            await transaction.CommitAsync(Ct);
        }
        var action = async () => await admission;
        (await action.Should().ThrowAsync<AuditStoreException>()).Which.Code.Should().Be("selected_admission_expired");
        (await _store.GetRunAsync(id, Ct))!.Attempts.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectedExecution_LostOwnerPreventsLatePublication(bool cancel)
    {
        var id = await Runs.CreateAsync(new(Scope(Guid.NewGuid()), [], false), "selected-late", Ct);
        var binding = await SelectedBinding(id);
        var gate = new SelectedVerifierGate();
        var worker = new AuditWorkerService(_db, _store, _clock, Options.Create(_audit), _options, gate);
        var service = new AuditSelectedRunService(_store, worker, Options.Create(_audit));
        var execution = service.ExecuteAsync(binding, Guid.NewGuid(), Now + Duration.FromHours(1), Ct);
        await gate.Entered.Task.WaitAsync(Ct);
        await using var other = new AiObservatoryDbContext(_options);
        var store = new AuditStore(other, _clock, _audit);
        try
        {
            if (cancel)
            {
                await store.CancelRunAsync(id, Ct);
            }
            else
            {
                _clock.Advance(Duration.FromMinutes(6));
            }
        }
        finally
        {
            gate.Release.TrySetResult();
        }
        (await execution).Should().Be("attempt_finished");
        var run = (await store.GetRunAsync(id, Ct))!;
        run.Result.Should().BeNull();
        run.State.Should().Be(cancel ? "cancelled" : "executing");
        var repeat = () => Selected.ExecuteAsync(binding, Guid.NewGuid(), Now + Duration.FromHours(1), Ct);
        await repeat.Should().ThrowAsync<AuditStoreException>();
    }

    private sealed class SelectedVerifierGate : IAuditRunVerifier
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AuditResult> VerifyAsync(AuditInput input, CancellationToken ct)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return new AuditVerifier().Verify(input);
        }
    }

    private sealed class SelectedLockSignal : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default
        )
        {
            if (command.CommandText.Contains("pg_advisory_xact_lock", StringComparison.Ordinal))
            {
                Entered.TrySetResult();
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class SelectedExecutionWitness : DbTransactionInterceptor, IAuditRunVerifier
    {
        private int _transactionsStarted;
        private int _verifierCalls;
        public int TransactionsStarted => Volatile.Read(ref _transactionsStarted);
        public int VerifierCalls => Volatile.Read(ref _verifierCalls);

        public override ValueTask<DbTransaction> TransactionStartedAsync(
            DbConnection connection,
            TransactionEndEventData eventData,
            DbTransaction result,
            CancellationToken cancellationToken = default
        )
        {
            Interlocked.Increment(ref _transactionsStarted);
            return ValueTask.FromResult(result);
        }

        public Task<AuditResult> VerifyAsync(AuditInput input, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _verifierCalls);
            return Task.FromResult(new AuditVerifier().Verify(input));
        }
    }

    private sealed class SelectedCommitFailure(bool committed, bool enabled = true) : DbTransactionInterceptor
    {
        private int _remaining = enabled ? 1 : 0;
        private int _faultsTriggered;
        public int FaultsTriggered => Volatile.Read(ref _faultsTriggered);

        private bool TakeFault()
        {
            if (Interlocked.Exchange(ref _remaining, 0) != 1)
            {
                return false;
            }
            Interlocked.Increment(ref _faultsTriggered);
            return true;
        }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default
        )
        {
            if (!committed && TakeFault())
            {
                throw new InvalidOperationException("selected_before_commit");
            }
            return ValueTask.FromResult(result);
        }

        public override Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default
        )
        {
            if (committed && TakeFault())
            {
                throw new InvalidOperationException("selected_commit_outcome_unknown");
            }
            return Task.CompletedTask;
        }
    }
}
