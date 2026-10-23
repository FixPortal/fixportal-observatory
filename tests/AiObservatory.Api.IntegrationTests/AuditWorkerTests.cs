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
using NodaTime.Testing;
using Npgsql;
using AuditOptions = AiObservatory.Api.Audit.AuditOptions;

namespace AiObservatory.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed partial class AuditWorkerTests : IAsyncLifetime
{
    private static readonly Instant Now = Instant.FromUtc(2026, 10, 22, 12, 0);
    private readonly FakeClock _clock = new(Now);
    private readonly CancellationTokenSource _ceiling = CancellationTokenSource.CreateLinkedTokenSource(
        TestContext.Current.CancellationToken
    );
    private DbContextOptions<AiObservatoryDbContext> _options = null!;
    private AiObservatoryDbContext _db = null!;
    private AuditStore _store = null!;
    private readonly AuditOptions _audit = new() { Enabled = true, WorkerEnabled = false };
    private CancellationToken Ct => _ceiling.Token;

    public async ValueTask InitializeAsync()
    {
        _ceiling.CancelAfter(TimeSpan.FromSeconds(45));
        var conn = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TEST_DB_CONNECTION"))
        {
            Database = $"aiobs_test_worker_{Guid.NewGuid():N}",
        }.ConnectionString;
        await AiObservatory.Testing.PostgresTestAssemblyFixture.CloneMigratedDatabaseAsync(conn, Ct);
        _options = new DbContextOptionsBuilder<AiObservatoryDbContext>().UseNpgsql(conn, o => o.UseNodaTime()).Options;
        _db = new(_options);
        await _db.Database.MigrateAsync(Ct);
        _store = new(_db, _clock, _audit);
    }

    public async ValueTask DisposeAsync()
    {
        await _db.Database.EnsureDeletedAsync(CancellationToken.None);
        await _db.DisposeAsync();
        _ceiling.Dispose();
    }

    private static AuditScope Scope(Guid machine) =>
        new(new(2026, 10, 1), new(2026, 10, 7), [new(machine, "claude-local")], [CheckCode.SourceAccounting], [], true);

    private AuditRunService Runs => new(_store, _clock);
    private AuditWorkerService Worker => new(_db, _store, _clock, Options.Create(_audit), _options);

    [Fact]
    public async Task SelectedExecution_DoesNotRefreshUnrelatedCollectingRun()
    {
        var machine = await _store.RegisterMachineAsync(new byte[32], Ct);
        var unrelated = await Runs.CreateAsync(new(Scope(machine.MachineId), [], true), "selected-witness-other", Ct);
        var selected = await Runs.CreateAsync(new(Scope(Guid.NewGuid()), [], false), "selected-witness-target", Ct);
        var before = (await _store.GetRunAsync(unrelated, Ct))!;

        var selectedRun = (await _store.GetRunAsync(selected, Ct))!;
        var service = new AuditSelectedRunService(_store, Worker, Options.Create(_audit));
        (
            await service.ExecuteAsync(
                new(selected, AuditCanonicalJson.ContentHash(selectedRun.Scope), selectedRun.Revision),
                Guid.NewGuid(),
                Now + Duration.FromHours(1),
                Ct
            )
        )
            .Should()
            .Be("attempt_finished");

        var after = (await _store.GetRunAsync(unrelated, Ct))!;
        after.State.Should().Be(before.State);
        after.Stage.Should().Be(before.Stage);
        after.Revision.Should().Be(before.Revision);
        after
            .DiagnosticCode.Should()
            .Be(
                before.DiagnosticCode,
                "executing one selected run must not write capture_waiting to another collecting run"
            );
        (await _store.GetRunAsync(selected, Ct))!.State.Should().Be("completed");
    }

    [Fact]
    public async Task Worker_NoCaptureStillFreezesAndCannotMatch()
    {
        var id = await Runs.CreateAsync(new(Scope(Guid.NewGuid()), [], false), "empty", Ct);
        (await Worker.ExecuteOneAsync(Guid.NewGuid(), Ct)).Should().BeTrue();
        var run = await _store.GetRunAsync(id, Ct);
        run!.State.Should().Be("completed");
        run.ServerCutoff.Should().Be(Now);
        var snapshot = await _store.ReadSnapshotAsync(id, Ct);
        snapshot.Should().NotBeNull();
        var result = AuditCanonicalJson.Deserialize<AuditResult>(run.Result!);
        result.Verdict.Should().Be(CheckVerdict.Incomplete);
        result.InputContentDigest.Should().Be(snapshot.InputContentDigest);
    }

    [Fact]
    public async Task OfflineMachine_WaitsThenFailsAtDeadline()
    {
        var machine = await _store.RegisterMachineAsync(new byte[32], Ct);
        var id = await Runs.CreateAsync(new(Scope(machine.MachineId), [], true), "offline", Ct);
        await Worker.ExecuteOneAsync(Guid.NewGuid(), Ct);
        (await _store.GetRunAsync(id, Ct))!.State.Should().Be("collecting");
        _clock.Advance(Duration.FromHours(24));
        await Worker.ExecuteOneAsync(Guid.NewGuid(), Ct);
        var run = await _store.GetRunAsync(id, Ct);
        run!.State.Should().Be("failed");
        run.DiagnosticCode.Should().Be("capture_deadline");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CancelCollectingRun_ReleasesCaptureCapacityAndFencesUploads(bool claimed, bool revisioned)
    {
        _audit.MaxOutstandingRequestsPerMachine = 1;
        var machine = await _store.RegisterMachineAsync(new byte[32], Ct);
        var id = await Runs.CreateAsync(new(Scope(machine.MachineId), [], true), "cancel-capture", Ct);
        var lease = claimed
            ? (await _store.TryClaimCaptureAsync(machine.MachineId, machine.CredentialId, Now, Ct))!
            : null;
        if (revisioned)
        {
            (await _store.CancelRunAsync(id, (await _store.GetRunAsync(id, Ct))!.Revision, Ct)).Should().BeTrue();
        }
        else
        {
            await _store.CancelRunAsync(id, Ct);
        }
        (await _store.TryClaimCaptureAsync(machine.MachineId, machine.CredentialId, Now, Ct)).Should().BeNull();
        await Runs.CreateAsync(new(Scope(machine.MachineId), [], true), "next-capture", Ct);
        if (lease != null)
        {
            byte[] bytes =
            [
                .. AuditCanonicalJson.Serialize(
                    new SourceUsageRecord
                    {
                        MachineId = machine.MachineId,
                        SourceId = "claude-local",
                        SourceFormat = "claude-jsonl",
                        SourceVersion = "1",
                        Tool = SourceTool.Claude,
                        Kind = UsageRecordKind.SessionMetadata,
                        Generation = "g1",
                        FileId = "synthetic-file",
                        Sequence = 1,
                        Timestamp = Now.ToDateTimeOffset(),
                        Pricing = new(ServiceTier.Unknown, false, null, null),
                        Closure = new(ClosureState.Unknown, null, null),
                    }
                ),
                10,
            ];
            var upload = () => _store.PutChunkAsync(lease, 0, bytes, AuditCanonicalJson.ContentHash(bytes), Ct);
            (await upload.Should().ThrowAsync<AuditStoreException>()).Which.Code.Should().Be("capture_lease_conflict");
            var seal = () => Seal(lease);
            (await seal.Should().ThrowAsync<AuditStoreException>()).Which.Code.Should().Be("capture_lease_conflict");
        }
    }

    [Fact]
    public async Task CancelCollectingRun_PreservesRequestNeededByAnotherActiveRun()
    {
        var machine = await _store.RegisterMachineAsync(new byte[32], Ct);
        var first = await Runs.CreateAsync(new(Scope(machine.MachineId), [], true), "shared-first", Ct);
        var lease = (await _store.TryClaimCaptureAsync(machine.MachineId, machine.CredentialId, Now, Ct))!;
        var second = await Runs.CreateAsync(new(Scope(machine.MachineId), [], false), "shared-second", Ct);
        // Arrange a shared dependency directly: the public wizard currently creates distinct requests.
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"AuditRuns\" SET \"CaptureRequestIds\" = {new[] { lease.RequestId }}, \"State\" = 'collecting' WHERE \"Id\" = {second}",
            Ct
        );
        _db.ChangeTracker.Clear();
        await _store.CancelRunAsync(first, Ct);
        await Seal(lease);
        await Worker.ExecuteOneAsync(Guid.NewGuid(), Ct);
        (await _store.GetRunAsync(second, Ct))!.State.Should().Be("completed");
    }

    [Fact]
    public async Task Publication_RequiresImmutableInput()
    {
        var scope = Scope(Guid.NewGuid());
        var id = await _store.CreateRunAsync(scope, [], "unfrozen", false, Now, Ct);
        var lease = await _store.TryLeaseRunAsync(Guid.NewGuid(), Now, Ct);
        var result = new AuditVerifier().Verify(
            new()
            {
                SchemaVersion = 1,
                VerifierVersion = "1",
                RuleVersion = "1",
                Scope = scope,
            }
        );
        (await _store.TryPublishAsync(lease!, result, Ct)).Should().BeFalse();
        (await _store.GetRunAsync(id, Ct))!.Result.Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FrozenEvaluation_CancelOrExpiryFencesPublication(bool cancel)
    {
        var id = await _store.CreateRunAsync(Scope(Guid.NewGuid()), [], "fenced", false, Now, Ct);
        var lease = (await _store.TryLeaseRunAsync(Guid.NewGuid(), Now, Ct))!;
        await new AuditSnapshotService(_db, _store, lease).CreateAsync(id, Ct);
        var pinned = (await _store.ReadSnapshotAsync(id, Ct))!;
        var result = new AuditVerifier().Verify(AuditCanonicalJson.Deserialize<AuditInput>(pinned.Bytes));
        if (cancel)
        {
            await _store.CancelRunAsync(id, lease.Revision, Ct);
        }
        else
        {
            _clock.Advance(Duration.FromMinutes(6));
        }
        (await _store.TryPublishAsync(lease, result, Ct)).Should().BeFalse();
        (await _store.GetRunAsync(id, Ct))!.Result.Should().BeNull();
    }

    [Fact]
    public async Task Restart_ReusesExactPinnedInputAfterLiveRowsChange()
    {
        var id = await _store.CreateRunAsync(Scope(Guid.NewGuid()), [], "restart", false, Now, Ct);
        var lease = (await _store.TryLeaseRunAsync(Guid.NewGuid(), Now, Ct))!;
        await new AuditSnapshotService(_db, _store, lease).CreateAsync(id, Ct);
        var pinned = (await _store.ReadSnapshotAsync(id, Ct))!;
        _db.UsageEvents.Add(
            new AiObservatory.Data.Entities.UsageEvent
            {
                Provider = AiObservatory.Data.Entities.Provider.OpenAI,
                Model = "synthetic",
                EventKey = "after-freeze",
                SourceId = "claude-local",
                SourceKind = AiObservatory.Data.Entities.SourceKind.LocalTelemetry,
                UsageScope = AiObservatory.Data.Entities.UsageScope.Api,
                CostBasis = AiObservatory.Data.Entities.CostBasis.ListPriceEstimate,
                InputTokens = 987,
                OccurredAt = Instant.FromUtc(2026, 10, 3, 12, 0),
                ObservedAt = Now,
                IngestedAt = Now,
            }
        );
        await _db.SaveChangesAsync(Ct);
        _clock.Advance(Duration.FromMinutes(6));
        await using var restarted = new AiObservatoryDbContext(_options);
        var store = new AuditStore(restarted, _clock, _audit);
        await new AuditWorkerService(restarted, store, _clock, Options.Create(_audit), _options).ExecuteOneAsync(
            Guid.NewGuid(),
            Ct
        );
        (await store.ReadSnapshotAsync(id, Ct))!.Bytes.Should().Equal(pinned.Bytes);
        (await store.GetRunAsync(id, Ct))!.State.Should().Be("completed");
    }

    [Fact]
    public async Task Restart_IncompatiblePinnedIdentityFailsPermanentlyWithoutChangingSnapshot()
    {
        var scope = Scope(Guid.NewGuid());
        var id = await _store.CreateRunAsync(scope, [], "old-pinned-restart", false, Now, Ct);
        var lease = (await _store.TryLeaseRunAsync(Guid.NewGuid(), Now, Ct))!;
        var old = new AuditInput
        {
            SchemaVersion = 1,
            VerifierVersion = "1.0.0",
            RuleVersion = "1",
            Scope = scope,
        };
        var bytes = AuditCanonicalJson.Serialize(old);
        var digest = AuditComputationInput.ContentDigest(old);
        (await _store.TryStoreSnapshotAsync(lease, bytes, digest, Ct)).Should().BeTrue();
        var pinned = (await _store.ReadSnapshotAsync(id, Ct))!;
        _clock.Advance(Duration.FromMinutes(6));
        await using var restarted = new AiObservatoryDbContext(_options);
        var store = new AuditStore(restarted, _clock, _audit);
        var worker = new AuditWorkerService(restarted, store, _clock, Options.Create(_audit), _options);
        (await worker.ExecuteOneAsync(Guid.NewGuid(), Ct)).Should().BeTrue();
        var run = (await store.GetRunAsync(id, Ct))!;
        run.State.Should().Be("failed");
        run.DiagnosticCode.Should().Be("incompatible_audit_verifier");
        run.Result.Should().BeNull();
        var retained = (await store.ReadSnapshotAsync(id, Ct))!;
        retained.Bytes.Should().Equal(bytes);
        retained.Sha256.Should().Be(pinned.Sha256);
        retained.InputContentDigest.Should().Be(digest);
        (await worker.ExecuteOneAsync(Guid.NewGuid(), Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task PartialProceed_MixedSealedAndOfflineMachineFreezesOnlyAcknowledgedCaptures()
    {
        var ready = await _store.RegisterMachineAsync(new byte[32], Ct);
        var offline = await _store.RegisterMachineAsync(new byte[32], Ct);
        var scope = Scope(ready.MachineId) with
        {
            Sources = [new(ready.MachineId, "claude-local"), new(offline.MachineId, "codex-local")],
        };
        var id = await Runs.CreateAsync(new(scope, [], true), "partial", Ct);
        var claim = (await _store.TryClaimCaptureAsync(ready.MachineId, ready.CredentialId, Now, Ct))!;
        var capture = await Seal(claim);
        await Worker.ExecuteOneAsync(Guid.NewGuid(), Ct);
        (await _store.GetRunAsync(id, Ct))!.State.Should().Be("collecting");
        await _store.ProceedRunAsync(id, Ct);
        await Worker.ExecuteOneAsync(Guid.NewGuid(), Ct);
        var run = (await _store.GetRunAsync(id, Ct))!;
        run.State.Should().Be("completed");
        var pinned = AuditCanonicalJson.Deserialize<AuditInput>((await _store.ReadSnapshotAsync(id, Ct))!.Bytes);
        pinned.Captures.Should().ContainSingle(c => c.CaptureId == capture);
        pinned.PartialEvidenceAcknowledged.Should().BeTrue();
        new AuditVerifier().Verify(pinned).Verdict.Should().Be(CheckVerdict.Incomplete);
    }

    [Fact]
    public async Task Renewal_PreservesOwnershipRevisionAndDatabaseExpiryOverridesCachedLease()
    {
        var id = await Runs.CreateAsync(new(Scope(Guid.NewGuid()), [], false), "renew", Ct);
        var lease = (await _store.TryLeaseRunAsync(Guid.NewGuid(), Now, Ct))!;
        _clock.Advance(Duration.FromMinutes(4));
        (await _store.RenewRunLeaseAsync(lease, _clock.GetCurrentInstant(), Ct)).Should().BeTrue();
        (await _store.SetRunStageAsync(lease, "snapshotting", Ct)).Should().BeTrue();
        (await _store.GetRunAsync(id, Ct))!.Revision.Should().Be(lease.Revision);
        _clock.Advance(Duration.FromMinutes(2));
        (await _store.SetRunStageAsync(lease, "verifying", Ct))
            .Should()
            .BeTrue("database renewal survives the cached ExpiresAt");
        await new AuditSnapshotService(_db, _store, lease).CreateAsync(id, Ct);
    }

    [Fact]
    public async Task ConcurrentWorkers_OneClaimAndOnePublication()
    {
        var id = await Runs.CreateAsync(new(Scope(Guid.NewGuid()), [], false), "concurrent", Ct);
        await using var other = new AiObservatoryDbContext(_options);
        var second = new AuditWorkerService(
            other,
            new(other, _clock, _audit),
            _clock,
            Options.Create(_audit),
            _options
        );
        var results = await Task.WhenAll(
            Worker.ExecuteOneAsync(Guid.NewGuid(), Ct),
            second.ExecuteOneAsync(Guid.NewGuid(), Ct)
        );
        results.Count(r => r).Should().Be(1);
        (await _store.GetRunAsync(id, Ct))!.Attempts.Should().Be(1);
        var count = await _db
            .Database.SqlQueryRaw<int>("SELECT count(*)::integer AS \"Value\" FROM \"AuditServerSnapshots\"")
            .SingleAsync(Ct);
        count.Should().Be(1);
    }

    [Fact]
    public async Task CancellationDuringSnapshot_PreventsPublicationWithoutHoldingEvidenceLock()
    {
        var id = await Runs.CreateAsync(new(Scope(Guid.NewGuid()), [], false), "cancel-snapshot", Ct);
        var gate = new SnapshotGate();
        await using var snapshotDb = new AiObservatoryDbContext(
            new DbContextOptionsBuilder<AiObservatoryDbContext>(_options).AddInterceptors(gate).Options
        );
        var executing = new AuditWorkerService(
            snapshotDb,
            new(snapshotDb, _clock, _audit),
            _clock,
            Options.Create(_audit),
            _options
        ).ExecuteOneAsync(Guid.NewGuid(), Ct);
        await gate.Reached.Task.WaitAsync(Ct);
        try
        {
            await _store.CancelRunAsync(id, Ct);
        }
        finally
        {
            gate.Release.TrySetResult();
        }
        await executing;
        var run = (await _store.GetRunAsync(id, Ct))!;
        run.State.Should().Be("cancelled");
        run.Result.Should().BeNull();
    }

    [Fact]
    public async Task DeletionAfterFreeze_FencesPublicationAndReplay()
    {
        var machine = await _store.RegisterMachineAsync(new byte[32], Ct);
        await _store.CreateCaptureRequestAsync(machine.MachineId, Scope(machine.MachineId), Now, Ct);
        var capture = await Seal(
            (await _store.TryClaimCaptureAsync(machine.MachineId, machine.CredentialId, Now, Ct))!
        );
        var id = await Runs.CreateAsync(new(Scope(machine.MachineId), [capture], false), "delete", Ct);
        var lease = (await _store.TryLeaseRunAsync(Guid.NewGuid(), Now, Ct))!;
        await new AuditSnapshotService(_db, _store, lease).CreateAsync(id, Ct);
        var input = AuditCanonicalJson.Deserialize<AuditInput>((await _store.ReadSnapshotAsync(id, Ct))!.Bytes);
        await _store.DeleteCaptureAsync(capture, await _store.CaptureDeletionImpactAsync(capture, Ct), Ct);
        (await _store.TryPublishAsync(lease, new AuditVerifier().Verify(input), Ct)).Should().BeFalse();
        (await _store.GetRunAsync(id, Ct))!.Result.Should().BeNull();
        var replay = () => Runs.ReplayAsync(id, "deleted-replay", Ct);
        (await replay.Should().ThrowAsync<AuditStoreException>()).Which.StatusCode.Should().Be(410);
    }

    [Fact]
    public async Task Replay_IsIdempotentAndPinsBytesAfterLiveEventsAndCatalogChange()
    {
        var id = await Runs.CreateAsync(new(Scope(Guid.NewGuid()), [], false), "original", Ct);
        await Worker.ExecuteOneAsync(Guid.NewGuid(), Ct);
        var pinned = (await _store.ReadSnapshotAsync(id, Ct))!;
        _db.UsageEvents.Add(
            new AiObservatory.Data.Entities.UsageEvent
            {
                Provider = AiObservatory.Data.Entities.Provider.OpenAI,
                Model = "changed-live",
                EventKey = "replay-live",
                SourceId = "claude-local",
                SourceKind = AiObservatory.Data.Entities.SourceKind.LocalTelemetry,
                UsageScope = AiObservatory.Data.Entities.UsageScope.Api,
                CostBasis = AiObservatory.Data.Entities.CostBasis.ListPriceEstimate,
                InputTokens = 500,
                OccurredAt = Instant.FromUtc(2026, 10, 3, 12, 0),
                ObservedAt = Now,
                IngestedAt = Now,
            }
        );
        _db.PricingSnapshots.Add(
            new AiObservatory.Data.Entities.PricingSnapshot
            {
                Provider = AiObservatory.Data.Entities.Provider.OpenAI,
                SourceId = "claude-local",
                RetrievedAt = Now,
                SourceUrl = "https://example.invalid/synthetic",
                ContentHash = new string('a', 64),
                RawEvidence = "synthetic",
                NormalizedCatalog = "{}",
                IsActive = true,
            }
        );
        await _db.SaveChangesAsync(Ct);
        var replay = await Runs.ReplayAsync(id, "replay", Ct);
        (await Runs.ReplayAsync(id, "replay", Ct)).Should().Be(replay);
        await Worker.ExecuteOneAsync(Guid.NewGuid(), Ct);
        (await _store.ReadSnapshotAsync(replay, Ct))!.Bytes.Should().Equal(pinned.Bytes);
        (await _store.GetRunAsync(replay, Ct))!.Result.Should().Equal((await _store.GetRunAsync(id, Ct))!.Result!);
    }

    [Fact]
    public async Task FailedSnapshots_RetryBoundIsDurableAndQueueRemainsBounded()
    {
        _audit.MaxQueuedRuns = 1;
        _audit.MaxSnapshotBytes = 32;
        var request = new CreateAuditRunRequest(Scope(Guid.NewGuid()), [], false);
        var id = await Runs.CreateAsync(request, "bounded", Ct);
        (await Runs.CreateAsync(request, "bounded", Ct)).Should().Be(id);
        var queued = () => Runs.CreateAsync(request, "overflow", Ct);
        (await queued.Should().ThrowAsync<AuditStoreException>()).Which.StatusCode.Should().Be(429);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Worker.ExecuteOneAsync(Guid.NewGuid(), Ct);
        }
        var run = (await _store.GetRunAsync(id, Ct))!;
        run.State.Should().Be("failed");
        run.Attempts.Should().Be(3);
        run.DiagnosticCode.Should().Be("snapshot_size_limit");
        run.Result.Should().BeNull();
        (await Worker.ExecuteOneAsync(Guid.NewGuid(), Ct)).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OverlappingCaptureIdentityGenerations_AmbiguousRefusedSameGenerationAccepted(bool ambiguous)
    {
        var machine = await _store.RegisterMachineAsync(new byte[32], Ct);
        var ids = new List<Guid>();
        foreach (var generation in new[] { "g1", ambiguous ? "g2" : "g1" })
        {
            await _store.CreateCaptureRequestAsync(machine.MachineId, Scope(machine.MachineId), Now, Ct);
            ids.Add(
                await Seal(
                    (await _store.TryClaimCaptureAsync(machine.MachineId, machine.CredentialId, Now, Ct))!,
                    generation
                )
            );
        }
        var create = () => Runs.CreateAsync(new(Scope(machine.MachineId), ids.ToArray(), false), "generations", Ct);
        if (ambiguous)
        {
            (await create.Should().ThrowAsync<AuditStoreException>())
                .Which.Code.Should()
                .Be("identity_generation_ambiguous");
        }
        else
        {
            await create();
            await Worker.ExecuteOneAsync(Guid.NewGuid(), Ct);
        }
        foreach (var id in ids)
        {
            (await _store.ReadCaptureAsync(id, Ct)).Should().NotBeNull();
        }
    }

    [Fact]
    public async Task Readiness_FailedOfflineRefreshAndStalePricingAreVisibleWithoutCaptureRequests()
    {
        _db.SourceSyncStates.Add(
            new AiObservatory.Data.Entities.SourceSyncState
            {
                SourceId = "claude-local",
                IsConfigured = true,
                IsAvailable = false,
                ConsecutiveFailureCount = 2,
                ExpectedRefreshIntervalSeconds = 60,
                LastSuccessAt = Now.Minus(Duration.FromDays(2)),
                LastAttemptAt = Now,
                LastError = "PRIVATE_NATIVE_ERROR_CANARY",
            }
        );
        await _db.SaveChangesAsync(Ct);
        var readiness = await new AuditReadinessService(_db, _store, _clock, Options.Create(_audit)).GetAsync(
            Scope(Guid.NewGuid()),
            Ct
        );
        readiness
            .Sources.Single()
            .DiagnosticCodes.Should()
            .Contain([
                "capture_missing",
                "source_unavailable",
                "source_refresh_failed",
                "source_stale",
                "pricing_stale",
            ]);
        System.Text.Json.JsonSerializer.Serialize(readiness).Should().NotContain("PRIVATE_NATIVE_ERROR_CANARY");
        (
            await _db
                .Database.SqlQueryRaw<int>("SELECT count(*)::integer AS \"Value\" FROM \"AuditCaptureRequests\"")
                .SingleAsync(Ct)
        )
            .Should()
            .Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task VerificationInFlight_CancellationOrExpiredOwnershipPreventsPublication(bool cancel)
    {
        var id = await Runs.CreateAsync(new(Scope(Guid.NewGuid()), [], false), "verify-fence", Ct);
        var gate = new VerificationGate();
        await using var workerDb = new AiObservatoryDbContext(_options);
        var executing = new AuditWorkerService(
            workerDb,
            new(workerDb, _clock, _audit),
            _clock,
            Options.Create(_audit),
            _options,
            gate
        ).ExecuteOneAsync(Guid.NewGuid(), Ct);
        await gate.Reached.Task.WaitAsync(Ct);
        try
        {
            (await _store.GetRunAsync(id, Ct))!.Stage.Should().Be("verifying");
            if (cancel)
            {
                await _store.CancelRunAsync(id, Ct);
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
        await executing;
        (await _store.GetRunAsync(id, Ct))!.Result.Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Retention_VerificationInFlight_DeletionOrEvidenceExpiryFencesLiveWorker(bool delete)
    {
        var machine = await _store.RegisterMachineAsync(new byte[32], Ct);
        await _store.CreateCaptureRequestAsync(machine.MachineId, Scope(machine.MachineId), Now, Ct);
        var capture = await Seal(
            (await _store.TryClaimCaptureAsync(machine.MachineId, machine.CredentialId, Now, Ct))!
        );
        _clock.Advance(Duration.FromDays(90) - Duration.FromMinutes(1));
        var id = await Runs.CreateAsync(new(Scope(machine.MachineId), [capture], false), "retention-race", Ct);
        var gate = new VerificationGate();
        await using var workerDb = new AiObservatoryDbContext(_options);
        var executing = new AuditWorkerService(
            workerDb,
            new(workerDb, _clock, _audit),
            _clock,
            Options.Create(_audit),
            _options,
            gate
        ).ExecuteOneAsync(Guid.NewGuid(), Ct);
        await gate.Reached.Task.WaitAsync(Ct);
        try
        {
            if (delete)
            {
                await _store.DeleteCaptureAsync(capture, await _store.CaptureDeletionImpactAsync(capture, Ct), Ct);
            }
            else
            {
                _clock.Advance(Duration.FromMinutes(1));
                await _store.ExpireAsync(_clock.GetCurrentInstant(), Ct);
            }
        }
        finally
        {
            gate.Release.TrySetResult();
        }
        await executing;
        var run = (await _store.GetRunAsync(id, Ct))!;
        run.Result.Should().BeNull();
        run.State.Should().Be("failed");
        var replay = () => Runs.ReplayAsync(id, "retention-race-replay", Ct);
        (await replay.Should().ThrowAsync<AuditStoreException>()).Which.StatusCode.Should().Be(410);
    }

    private sealed class VerificationGate : IAuditRunVerifier
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AuditResult> VerifyAsync(AuditInput input, CancellationToken ct)
        {
            Reached.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return new AuditVerifier().Verify(input);
        }
    }

    [Fact]
    public async Task Readiness_PricingFreshnessUsesProviderAndDoesNotHideAnotherStaleProvider()
    {
        foreach (
            var row in new[]
            {
                (AiObservatory.Data.Entities.Provider.Anthropic, "anthropic-pricing", Now),
                (AiObservatory.Data.Entities.Provider.OpenAI, "openai-pricing", Now.Minus(Duration.FromDays(3))),
            }
        )
        {
            _db.PricingSnapshots.Add(
                new AiObservatory.Data.Entities.PricingSnapshot
                {
                    Provider = row.Item1,
                    SourceId = row.Item2,
                    RetrievedAt = row.Item3,
                    SourceUrl = "https://example.invalid/synthetic",
                    ContentHash = new string('a', 64),
                    RawEvidence = "synthetic",
                    NormalizedCatalog = "{}",
                    IsActive = true,
                }
            );
        }
        await _db.SaveChangesAsync(Ct);
        var scope = Scope(Guid.NewGuid());
        scope = scope with { Sources = [.. scope.Sources, new(scope.Sources[0].MachineId, "codex-local")] };
        var readiness = await new AuditReadinessService(_db, _store, _clock, Options.Create(_audit)).GetAsync(
            scope,
            Ct
        );
        readiness
            .Sources.Single(s => s.SourceId == "claude-local")
            .DiagnosticCodes.Should()
            .NotContain("pricing_stale");
        readiness.Sources.Single(s => s.SourceId == "codex-local").DiagnosticCodes.Should().Contain("pricing_stale");
    }

    [Theory]
    [InlineData("active", "missing_closure")]
    [InlineData("format", "unsupported_source_format")]
    [InlineData("failed", "incomplete_capture")]
    public async Task FrozenEvidence_OpenSessionUnsupportedFormatOrFailedScanCannotMatch(string kind, string diagnostic)
    {
        var machine = await _store.RegisterMachineAsync(new byte[32], Ct);
        await _store.CreateCaptureRequestAsync(machine.MachineId, Scope(machine.MachineId), Now, Ct);
        var lease = (await _store.TryClaimCaptureAsync(machine.MachineId, machine.CredentialId, Now, Ct))!;
        var record = new SourceUsageRecord
        {
            MachineId = machine.MachineId,
            SourceId = "claude-local",
            SourceFormat = "claude-jsonl",
            SourceVersion = kind == "format" ? "future" : "1",
            Tool = SourceTool.Claude,
            Kind = UsageRecordKind.MessageUsage,
            Generation = "g1",
            FileId = "file1",
            SessionId = "session1",
            MessageId = "message1",
            Sequence = 1,
            Timestamp = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero),
            Provider = "anthropic",
            Model = "synthetic",
            Counters = new ClaudeCounters(100, 20, 0, 0, 0, 0),
            Pricing = new(ServiceTier.Standard, false, null, null),
            Closure = new(ClosureState.Unknown, null, null),
        };
        byte[] bytes = [.. AuditCanonicalJson.Serialize(record), 10];
        var hash = AuditCanonicalJson.ContentHash(bytes);
        await _store.PutChunkAsync(lease, 0, bytes, hash, Ct);
        var manifest = new CaptureManifest(
            1,
            Guid.NewGuid(),
            lease.RequestId,
            machine.MachineId,
            "g1",
            "1",
            Now.ToDateTimeOffset(),
            Now.ToDateTimeOffset(),
            lease.Scope,
            [new("claude-local", "claude-jsonl", record.SourceVersion)],
            kind == "failed" ? 2 : 1,
            1,
            kind == "failed" ? 1 : 0,
            0,
            1,
            new(2026, 10, 3),
            new(2026, 10, 3),
            [],
            [hash],
            Now.ToDateTimeOffset()
        );
        var capture = await _store.SealCaptureAsync(lease, AuditCanonicalJson.Serialize(manifest), "seal", Ct);
        var id = await Runs.CreateAsync(new(Scope(machine.MachineId), [capture], false), "qualified", Ct);
        await Worker.ExecuteOneAsync(Guid.NewGuid(), Ct);
        var result = AuditCanonicalJson.Deserialize<AuditResult>((await _store.GetRunAsync(id, Ct))!.Result!);
        result.Verdict.Should().Be(CheckVerdict.Incomplete);
        result.Coverage.Gaps.Should().Contain(g => g.DiagnosticCode == diagnostic);
        var pinned = AuditCanonicalJson.Deserialize<AuditInput>((await _store.ReadSnapshotAsync(id, Ct))!.Bytes);
        AuditCanonicalJson
            .Serialize(new AuditVerifier().Verify(pinned))
            .Should()
            .Equal(AuditCanonicalJson.Serialize(result));
    }

    [Fact]
    public async Task Replay_NewerInstalledVerifierKeepsFrozenFactsAndCreatesNewInputIdentity()
    {
        var scope = Scope(Guid.NewGuid());
        var id = await Runs.CreateAsync(new(scope, [], false), "older-verifier", Ct);
        var lease = (await _store.TryLeaseRunAsync(Guid.NewGuid(), Now, Ct))!;
        var older = new AuditInput
        {
            SchemaVersion = 1,
            VerifierVersion = "0.9.0",
            RuleVersion = AuditVerifierIdentity.RuleVersion,
            Scope = scope,
        };
        var oldBytes = AuditCanonicalJson.Serialize(older);
        var oldDigest = AuditComputationInput.ContentDigest(older);
        (await _store.TryStoreSnapshotAsync(lease, oldBytes, oldDigest, Ct)).Should().BeTrue();
        (await _store.TryPublishAsync(lease, new AuditVerifier().Verify(older), Ct)).Should().BeTrue();
        var replay = await Runs.ReplayAsync(id, "new-verifier", Ct);
        await Worker.ExecuteOneAsync(Guid.NewGuid(), Ct);
        var snapshot = (await _store.ReadSnapshotAsync(replay, Ct))!;
        var input = AuditCanonicalJson.Deserialize<AuditInput>(snapshot.Bytes);
        input.VerifierVersion.Should().Be(AuditVerifierIdentity.VerifierVersion);
        input.RuleVersion.Should().Be(AuditVerifierIdentity.RuleVersion);
        input.Events.Should().BeEmpty();
        input.Captures.Should().BeEmpty();
        (await _store.ReadSnapshotAsync(id, Ct))!.Bytes.Should().Equal(oldBytes);
        snapshot.InputContentDigest.Should().NotBe(oldDigest);
    }

    [Fact]
    public async Task FreshCaptureConflictingGeneration_FailsAffectedRunWithoutBlockingQueue()
    {
        var machine = await _store.RegisterMachineAsync(new byte[32], Ct);
        await _store.CreateCaptureRequestAsync(machine.MachineId, Scope(machine.MachineId), Now, Ct);
        var oldCapture = await Seal(
            (await _store.TryClaimCaptureAsync(machine.MachineId, machine.CredentialId, Now, Ct))!,
            "g1"
        );
        var bad = await Runs.CreateAsync(new(Scope(machine.MachineId), [oldCapture], true), "rotated-generation", Ct);
        await Seal((await _store.TryClaimCaptureAsync(machine.MachineId, machine.CredentialId, Now, Ct))!, "g2");
        var good = await Runs.CreateAsync(new(Scope(Guid.NewGuid()), [], false), "independent", Ct);
        await Worker.ExecuteOneAsync(Guid.NewGuid(), Ct);
        (await _store.GetRunAsync(bad, Ct))!.State.Should().Be("failed");
        (await _store.GetRunAsync(bad, Ct))!.DiagnosticCode.Should().Be("identity_generation_ambiguous");
        (await _store.GetRunAsync(good, Ct))!.State.Should().Be("completed");
    }

    [Fact]
    public async Task ReplayPreparation_ReleasesEvidenceLockAndRechecksParentRevision()
    {
        var id = await Runs.CreateAsync(new(Scope(Guid.NewGuid()), [], false), "parent-revision", Ct);
        var lease = (await _store.TryLeaseRunAsync(Guid.NewGuid(), Now, Ct))!;
        await new AuditSnapshotService(_db, _store, lease).CreateAsync(id, Ct);
        var gate = new ReplayPreparationGate();
        await using var replayDb = new AiObservatoryDbContext(
            new DbContextOptionsBuilder<AiObservatoryDbContext>(_options).AddInterceptors(gate).Options
        );
        var creating = new AuditRunService(new(replayDb, _clock, _audit), _clock).ReplayAsync(id, "revision-race", Ct);
        await gate.Reached.Task.WaitAsync(Ct);
        try
        {
            await _store.CancelRunAsync(id, Ct);
        }
        finally
        {
            gate.Release.TrySetResult();
        }
        var action = () => creating;
        (await action.Should().ThrowAsync<AuditStoreException>()).Which.Code.Should().Be("replay_input_changed");
    }

    private sealed class ReplayPreparationGate : DbTransactionInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _used;

        public override async Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default
        )
        {
            if (Interlocked.Exchange(ref _used, 1) == 0)
            {
                Reached.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
        }
    }

    [Fact]
    public async Task Readiness_FiltersMachineSourceAndDatesBeforeBoundingCaptureChoices()
    {
        _ceiling.CancelAfter(TimeSpan.FromSeconds(90));
        var selected = await _store.RegisterMachineAsync(new byte[32], Ct);
        var other = await _store.RegisterMachineAsync(new byte[32], Ct);
        var requested = Scope(selected.MachineId);
        async Task<Guid> Capture(MachineRegistration machine, AuditScope scope)
        {
            var now = _clock.GetCurrentInstant();
            await _store.CreateCaptureRequestAsync(machine.MachineId, scope, now, Ct);
            var lease = (await _store.TryClaimCaptureAsync(machine.MachineId, machine.CredentialId, now, Ct))!;
            var id = await Seal(lease);
            _clock.Advance(Duration.FromSeconds(1));
            return id;
        }
        var retained = await Capture(selected, requested);
        for (var index = 0; index < 100; index++)
        {
            await Capture(other, Scope(other.MachineId));
        }
        for (var index = 0; index < 100; index++)
        {
            await Capture(selected, requested with { From = new(2026, 10, 8), To = new(2026, 10, 14) });
        }
        await Capture(selected, requested with { Sources = [new(selected.MachineId, "unrelated-local")] });
        var service = new AuditReadinessService(_db, _store, _clock, Options.Create(_audit));
        var readiness = (await service.GetAsync(requested, Ct)).Sources.Single();
        readiness.CaptureIds.Should().Equal(retained);
        readiness.DiagnosticCodes.Should().NotContain("capture_missing");
        readiness.DiagnosticCodes.Should().NotContain("capture_choices_truncated");
        var allMachines = (
            await service.GetAsync(requested with { Sources = [new(Guid.Empty, "claude-local")] }, Ct)
        ).Sources.Single();
        allMachines.CaptureIds.Should().HaveCount(101).And.Contain(retained);
        for (var index = 0; index < 201; index++)
        {
            await Capture(selected, requested);
        }
        var bounded = (await service.GetAsync(requested, Ct)).Sources.Single();
        bounded.CaptureIds.Should().HaveCount(200).And.NotContain(retained);
        bounded.DiagnosticCodes.Should().Contain("capture_choices_truncated").And.NotContain("capture_missing");
    }

    private async Task<Guid> Seal(CaptureLease lease, string generation = "g1")
    {
        var manifest = new CaptureManifest(
            1,
            Guid.NewGuid(),
            lease.RequestId,
            lease.MachineId,
            generation,
            "1",
            Now.ToDateTimeOffset(),
            Now.ToDateTimeOffset(),
            lease.Scope,
            lease
                .Scope.Sources.Select(s => new SourceFormat(
                    s.SourceId,
                    s.SourceId == "codex-local" ? "codex-jsonl" : "claude-jsonl",
                    "1"
                ))
                .ToArray(),
            0,
            0,
            0,
            0,
            0,
            null,
            null,
            [],
            [],
            Now.ToDateTimeOffset()
        );
        return await _store.SealCaptureAsync(lease, AuditCanonicalJson.Serialize(manifest), "seal", Ct);
    }

    private sealed class SnapshotGate : DbCommandInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default
        )
        {
            if (command.CommandText.Contains("LEFT JOIN \"PricingApplications\"", StringComparison.Ordinal))
            {
                Reached.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}
