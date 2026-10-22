using AiObservatory.Audit;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using AiObservatory.Data.Audit;
using AiObservatory.Data.Entities;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;
using NodaTime.Testing;
using Npgsql;

namespace AiObservatory.Data.Tests.Audit;

/// <summary>Exercises durable coordination against isolated real PostgreSQL databases.</summary>
[Trait("Category", "Integration")]
public sealed class AuditStoreTests : IAsyncLifetime
{
    private static readonly Instant Now = Instant.FromUtc(2026, 10, 22, 12, 0);
    private readonly FakeClock _clock = new(Now);
    private readonly CancellationTokenSource _ceiling = CancellationTokenSource.CreateLinkedTokenSource(
        TestContext.Current.CancellationToken
    );
    private DbContextOptions<AiObservatoryDbContext> _options = null!;
    private AiObservatoryDbContext _db = null!;
    private AuditStore _store = null!;
    private CancellationToken Ct => _ceiling.Token;

    public async ValueTask InitializeAsync()
    {
        _ceiling.CancelAfter(TimeSpan.FromSeconds(30));
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TEST_DB_CONNECTION"))
        {
            Database = $"aiobs_test_audit_{Guid.NewGuid():N}",
        }.ConnectionString;
        _options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(connection, o => o.UseNodaTime())
            .Options;
        _db = new(_options);
        await _db.Database.MigrateAsync(Ct);
        _store = new(_db, _clock);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _db.Database.EnsureDeletedAsync(CancellationToken.None);
        }
        finally
        {
            await _db.DisposeAsync();
            _ceiling.Dispose();
        }
    }

    /// <summary>A concurrent claim must never allow two workers to execute one queued run.</summary>
    [Fact]
    public async Task TryLeaseRun_TwoWorkers_OnlyOneOwnsRun()
    {
        var id = await Run();
        await using var otherDb = new AiObservatoryDbContext(_options);
        var claims = await Task.WhenAll(
            _store.TryLeaseRunAsync(Guid.NewGuid(), Now, Ct),
            new AuditStore(otherDb, _clock).TryLeaseRunAsync(Guid.NewGuid(), Now, Ct)
        );
        claims.OfType<RunLease>().Should().ContainSingle().Which.RunId.Should().Be(id);
    }

    /// <summary>The execution capacity belongs to an instance, rather than the entire database.</summary>
    [Fact]
    public async Task TryLeaseRun_AnotherInstance_DoesNotConsumeItsCapacity()
    {
        await Run();
        var second = new AuditStore(_db, _clock, instanceId: "second");
        var id = await second.CreateRunAsync(Scope(), [], "second-key", false, Now, Ct);
        (await _store.TryLeaseRunAsync(Guid.NewGuid(), Now, Ct)).Should().NotBeNull();
        var secondLease = await second.TryLeaseRunAsync(Guid.NewGuid(), Now, Ct);
        secondLease.Should().NotBeNull();
        secondLease.RunId.Should().Be(id);
    }

    /// <summary>Expiry transfers ownership and fences the stale worker, including before retry claim.</summary>
    [Fact]
    public async Task TryPublish_ExpiredAndRetriedLease_RejectsStaleWorker()
    {
        await Run();
        var old = (await _store.TryLeaseRunAsync(Guid.NewGuid(), Now, Ct))!;
        _clock.Advance(Duration.FromMinutes(5));
        (await _store.TryPublishAsync(old, Result(), Ct)).Should().BeFalse();
        var next = (await _store.TryLeaseRunAsync(Guid.NewGuid(), _clock.GetCurrentInstant(), Ct))!;
        next.Revision.Should().BeGreaterThan(old.Revision);
        (await _store.RenewRunLeaseAsync(old, _clock.GetCurrentInstant(), Ct)).Should().BeFalse();
        (await _store.TryPublishAsync(old, Result(), Ct)).Should().BeFalse();
        (await PublishFrozen(next)).Should().BeTrue();
    }

    /// <summary>Renewal extends live ownership but cannot revive expired ownership.</summary>
    [Fact]
    public async Task RenewRunLease_LiveLease_ExtendsPastOriginalExpiry()
    {
        await Run();
        var lease = (await _store.TryLeaseRunAsync(Guid.NewGuid(), Now, Ct))!;
        _clock.Advance(Duration.FromMinutes(1));
        (await _store.RenewRunLeaseAsync(lease, _clock.GetCurrentInstant(), Ct)).Should().BeTrue();
        _clock.Advance(Duration.FromMinutes(4));
        (await PublishFrozen(lease)).Should().BeTrue();
    }

    /// <summary>A permanently lost worker consumes at most three attempts and leaves diagnostics.</summary>
    [Fact]
    public async Task TryLeaseRun_ThreeExpiredAttempts_FailsDurably()
    {
        var id = await Run();
        for (var i = 0; i < 3; i++)
        {
            (await _store.TryLeaseRunAsync(Guid.NewGuid(), _clock.GetCurrentInstant(), Ct)).Should().NotBeNull();
            _clock.Advance(Duration.FromMinutes(5));
        }
        (await _store.TryLeaseRunAsync(Guid.NewGuid(), _clock.GetCurrentInstant(), Ct)).Should().BeNull();
        var view = (await _store.GetRunAsync(id, Ct))!;
        view.State.Should().Be("failed");
        view.Attempts.Should().Be(3);
        view.DiagnosticCode.Should().Be("lease_expired");
    }

    /// <summary>Cancellation advances the same revision fence used by result publication.</summary>
    [Fact]
    public async Task CancelRun_WithLiveWorker_FencesPublication()
    {
        var id = await Run();
        var lease = (await _store.TryLeaseRunAsync(Guid.NewGuid(), Now, Ct))!;
        (await _store.CancelRunAsync(id, lease.Revision - 1, Ct)).Should().BeFalse();
        (await _store.CancelRunAsync(id, lease.Revision, Ct)).Should().BeTrue();
        (await _store.TryPublishAsync(lease, Result(), Ct)).Should().BeFalse();
        (await _store.GetRunAsync(id, Ct))!.State.Should().Be("cancelled");
    }

    /// <summary>Short queue locks enforce capacity without discarding existing or idempotent work.</summary>
    [Fact]
    public async Task CreateRun_QueueFull_RejectsNewAndRetainsIdenticalRetry()
    {
        var first = await _store.CreateRunAsync(Scope(), [], "first", false, Now, Ct);
        for (var i = 1; i < 20; i++)
        {
            await Run();
        }
        var action = () => Run();
        (await action.Should().ThrowAsync<AuditStoreException>()).Which.StatusCode.Should().Be(429);
        (await _store.CreateRunAsync(Scope(), [], "first", false, Now, Ct)).Should().Be(first);
        var conflict = () => _store.CreateRunAsync(Scope() with { AllowPartial = false }, [], "first", false, Now, Ct);
        (await conflict.Should().ThrowAsync<AuditStoreException>()).Which.StatusCode.Should().Be(409);
    }

    /// <summary>Only outstanding unexpired requests consume a machine's bounded capture queue.</summary>
    [Fact]
    public async Task CreateCaptureRequest_TwoOutstanding_RejectsThirdUntilDeadline()
    {
        var machine = await Machine();
        await _store.CreateCaptureRequestAsync(machine.MachineId, Scope(machine.MachineId), Now, Ct);
        await _store.CreateCaptureRequestAsync(machine.MachineId, Scope(machine.MachineId), Now, Ct);
        var action = () => _store.CreateCaptureRequestAsync(machine.MachineId, Scope(machine.MachineId), Now, Ct);
        (await action.Should().ThrowAsync<AuditStoreException>()).Which.StatusCode.Should().Be(429);
        _clock.Advance(Duration.FromHours(24));
        (
            await _store.CreateCaptureRequestAsync(
                machine.MachineId,
                Scope(machine.MachineId),
                _clock.GetCurrentInstant(),
                Ct
            )
        )
            .Should()
            .NotBeEmpty();
    }

    /// <summary>Sealing validates the complete sequence atomically and preserves the original bytes.</summary>
    [Fact]
    public async Task SealCapture_MissingChunkThenRetry_PreservesExactImmutableEvidence()
    {
        var lease = await Capture();
        var bytes = Record(lease.MachineId);
        var hash = AuditCanonicalJson.ContentHash(bytes);
        var manifest = Manifest(lease, [hash], 1);
        var action = () => _store.SealCaptureAsync(lease, manifest, "seal", Ct);
        (await action.Should().ThrowAsync<AuditStoreException>()).Which.StatusCode.Should().Be(409);
        await _store.PutChunkAsync(lease, 0, bytes, hash, Ct);
        await _store.PutChunkAsync(lease, 0, bytes, hash, Ct);
        var id = await _store.SealCaptureAsync(lease, manifest, "seal", Ct);
        (await _store.SealCaptureAsync(lease, manifest, "seal", Ct)).Should().Be(id);
        var retained = (await _store.ReadCaptureAsync(id, Ct))!;
        retained.Manifest.Should().Equal(manifest);
        retained.Chunks.Should().ContainSingle().Which.Should().Equal(bytes);
        retained.ExpiresAt.Should().Be(Now.Plus(Duration.FromDays(90)));
        var overwrite = () => _store.PutChunkAsync(lease, 0, bytes, hash, Ct);
        (await overwrite.Should().ThrowAsync<AuditStoreException>()).Which.StatusCode.Should().Be(409);
        var changedManifest = Manifest(lease, [hash], 2);
        var reseal = () => _store.SealCaptureAsync(lease, changedManifest, "seal", Ct);
        (await reseal.Should().ThrowAsync<AuditStoreException>()).Which.StatusCode.Should().Be(409);
    }

    /// <summary>A conflicting index or digest can never replace previously uploaded bytes.</summary>
    [Fact]
    public async Task PutChunk_ConflictingRetryOrHash_RejectsWithoutReplacing()
    {
        var lease = await Capture();
        var bytes = Record(lease.MachineId);
        await _store.PutChunkAsync(lease, 0, bytes, AuditCanonicalJson.ContentHash(bytes), Ct);
        var changed = Record(lease.MachineId, 2);
        var conflict = () => _store.PutChunkAsync(lease, 0, changed, AuditCanonicalJson.ContentHash(changed), Ct);
        (await conflict.Should().ThrowAsync<AuditStoreException>()).Which.StatusCode.Should().Be(409);
        var corrupt = () => _store.PutChunkAsync(lease, 1, changed, new string('0', 64), Ct);
        (await corrupt.Should().ThrowAsync<AuditStoreException>()).Which.StatusCode.Should().Be(400);
    }

    /// <summary>Count, ordering, and membership errors cannot leave a partly sealed capture.</summary>
    [Theory]
    [InlineData("count")]
    [InlineData("hash")]
    [InlineData("gap")]
    [InlineData("machine")]
    public async Task SealCapture_InvalidManifest_RejectsAtomically(string problem)
    {
        var lease = await Capture();
        var bytes = Record(problem == "machine" ? Guid.NewGuid() : lease.MachineId);
        var hash = AuditCanonicalJson.ContentHash(bytes);
        await _store.PutChunkAsync(lease, problem == "gap" ? 1 : 0, bytes, hash, Ct);
        var manifest = Manifest(lease, [problem == "hash" ? new string('0', 64) : hash], problem == "count" ? 2 : 1);
        var action = () => _store.SealCaptureAsync(lease, manifest, "seal", Ct);
        (await action.Should().ThrowAsync<AuditStoreException>()).Which.StatusCode.Should().Be(409);
    }

    /// <summary>Revocation cuts off an already issued upload lease immediately.</summary>
    [Fact]
    public async Task PutChunk_RevokedCredential_RejectsLiveLease()
    {
        var lease = await Capture();
        await _store.RevokeCredentialAsync(lease.CredentialId, Ct);
        var bytes = Record(lease.MachineId);
        var action = () => _store.PutChunkAsync(lease, 0, bytes, AuditCanonicalJson.ContentHash(bytes), Ct);
        (await action.Should().ThrowAsync<AuditStoreException>()).Which.StatusCode.Should().Be(403);
        var seal = () => _store.SealCaptureAsync(lease, Manifest(lease, [], 0), "seal", Ct);
        (await seal.Should().ThrowAsync<AuditStoreException>()).Which.StatusCode.Should().Be(403);
    }

    /// <summary>Deletion impact uses revisions so stale consent cannot delete newly referenced evidence.</summary>
    [Fact]
    public async Task DeleteCapture_ChangedImpact_FencesWorkerAndPreservesReportMetadata()
    {
        var lease = await Capture();
        var captureId = await _store.SealCaptureAsync(lease, Manifest(lease, [], 0), "seal", Ct);
        var digest = await _store.CaptureDeletionImpactAsync(captureId, Ct);
        var run = await _store.CreateRunAsync(Scope(lease.MachineId), [captureId], "linked", false, Now, Ct);
        var worker = (await _store.TryLeaseRunAsync(Guid.NewGuid(), Now, Ct))!;
        var stale = () => _store.DeleteCaptureAsync(captureId, digest, Ct);
        (await stale.Should().ThrowAsync<AuditStoreException>()).Which.StatusCode.Should().Be(412);
        await _store.DeleteCaptureAsync(captureId, await _store.CaptureDeletionImpactAsync(captureId, Ct), Ct);
        (await _store.ReadCaptureAsync(captureId, Ct)).Should().BeNull();
        (await _store.TryPublishAsync(worker, Result(lease.MachineId), Ct)).Should().BeFalse();
        (await _store.GetRunAsync(run, Ct))!.DiagnosticCode.Should().Be("evidence_deleted");
    }

    /// <summary>Indefinite settings apply only to future seals and completions, preserving prior deadlines.</summary>
    [Fact]
    public async Task UpdateRetention_NullFutureSettings_PreservesExistingExpiry()
    {
        var a = await Capture();
        var idA = await _store.SealCaptureAsync(a, Manifest(a, [], 0), "a", Ct);
        await _store.UpdateRetentionAsync(null, null, Ct);
        var b = await Capture();
        var idB = await _store.SealCaptureAsync(b, Manifest(b, [], 0), "b", Ct);
        (await _store.ReadCaptureAsync(idA, Ct))!.ExpiresAt.Should().Be(Now.Plus(Duration.FromDays(90)));
        (await _store.ReadCaptureAsync(idB, Ct))!.ExpiresAt.Should().BeNull();
        var run = await Run();
        var worker = (await _store.TryLeaseRunAsync(Guid.NewGuid(), Now, Ct))!;
        (await PublishFrozen(worker)).Should().BeTrue();
        (await _store.GetRunAsync(run, Ct))!.SummaryExpiresAt.Should().BeNull();
    }

    /// <summary>Distinct queued runs still share the single execution slot for one instance.</summary>
    [Fact]
    public async Task TryLeaseRun_TwoQueuedRuns_EnforcesInstanceExecutionLimit()
    {
        await Run();
        await Run();
        await using var otherDb = new AiObservatoryDbContext(_options);
        var claims = await Task.WhenAll(
            _store.TryLeaseRunAsync(Guid.NewGuid(), Now, Ct),
            new AuditStore(otherDb, _clock).TryLeaseRunAsync(Guid.NewGuid(), Now, Ct)
        );
        claims.OfType<RunLease>().Should().ContainSingle();
    }

    /// <summary>Explicit execution failure permits bounded retry and retains the final sanitized diagnostic.</summary>
    [Fact]
    public async Task FailRun_ThreeFailures_LeavesFinalDiagnosticAndRejectsOldLease()
    {
        var id = await Run();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var lease = (await _store.TryLeaseRunAsync(Guid.NewGuid(), Now, Ct))!;
            (await _store.FailRunAsync(lease, "verification_failed", Ct)).Should().BeTrue();
            (await _store.TryPublishAsync(lease, Result(), Ct)).Should().BeFalse();
        }
        var view = (await _store.GetRunAsync(id, Ct))!;
        view.State.Should().Be("failed");
        view.DiagnosticCode.Should().Be("verification_failed");
        view.SummaryExpiresAt.Should().Be(Now.Plus(Duration.FromDays(365)));
    }

    /// <summary>Snapshots are immutable, lease fenced, bound to publication, and retained from freezing.</summary>
    [Fact]
    public async Task TryStoreSnapshot_LiveLease_RejectsConflictingBytesAndUnboundResult()
    {
        var id = await Run();
        var lease = (await _store.TryLeaseRunAsync(Guid.NewGuid(), Now, Ct))!;
        byte[] bytes = [123, 125];
        var digest = new string('b', 64);
        (await _store.TryStoreSnapshotAsync(lease, bytes, digest, Ct)).Should().BeTrue();
        (await _store.TryStoreSnapshotAsync(lease, bytes, digest, Ct)).Should().BeTrue();
        var conflict = () => _store.TryStoreSnapshotAsync(lease, [91, 93], digest, Ct);
        (await conflict.Should().ThrowAsync<AuditStoreException>()).Which.StatusCode.Should().Be(409);
        var snapshot = (await _store.ReadSnapshotAsync(id, Ct))!;
        snapshot.Bytes.Should().Equal(bytes);
        snapshot.Sha256.Should().Be("44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a");
        snapshot.ExpiresAt.Should().Be(Now.Plus(Duration.FromDays(90)));
        var unbound = () => _store.TryPublishAsync(lease, Result(), Ct);
        (await unbound.Should().ThrowAsync<AuditStoreException>()).Which.StatusCode.Should().Be(409);
        (await _store.TryPublishAsync(lease, Result() with { InputContentDigest = digest }, Ct)).Should().BeTrue();
        (await _store.TryStoreSnapshotAsync(lease, bytes, digest, Ct)).Should().BeFalse();
    }

    /// <summary>Adding dormant audit storage to a populated installation leaves every original usage field unchanged.</summary>
    [Fact]
    public async Task Migration_PopulatedUsage_PreservesHistoricalFactsAndUnknownProvenance()
    {
        // This fixture owns the unique database, so reverting only its empty audit schema is safe.
        var migrator = _db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261019161337_AddSuggestions", Ct);
        var usage = new UsageEvent
        {
            Provider = Provider.Anthropic,
            OccurredAt = Now,
            IngestedAt = Now,
            InputTokens = 9007199254740993,
            OutputTokens = 12,
            CostUsd = 1.234567891234m,
            RawPayload = "{\"synthetic\":true}",
            Model = "synthetic-model",
            EventKey = "synthetic-event",
        };
        // Seed the historical schema through its historical columns. The current model also
        // maps Task8's later CurrentPricingRevision column, which does not exist at this point.
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "UsageEvents" ("Id","Provider","OccurredAt","IngestedAt","InputTokens","OutputTokens","CostUsd","RawPayload","Model","EventKey","SourceId","SourceKind","UsageScope","CostBasis","ObservedAt")
            VALUES ({usage.Id},{usage.Provider.ToString()},{usage.OccurredAt},{usage.IngestedAt},{usage.InputTokens},{usage.OutputTokens},{usage.CostUsd},{usage.RawPayload}::jsonb,{usage.Model},{usage.EventKey},{usage.SourceId},{usage.SourceKind.ToString()},{usage.UsageScope.ToString()},{usage.CostBasis.ToString()},{usage.ObservedAt})
            """,
            Ct
        );
        var before = await _db
            .Database.SqlQueryRaw<string>("SELECT to_jsonb(e)::text AS \"Value\" FROM \"UsageEvents\" e")
            .SingleAsync(Ct);
        await migrator.MigrateAsync(cancellationToken: Ct);
        var historicalAfter = await _db
            .Database.SqlQueryRaw<string>(
                "SELECT (to_jsonb(e)-'CurrentPricingRevision')::text AS \"Value\" FROM \"UsageEvents\" e"
            )
            .SingleAsync(Ct);
        historicalAfter.Should().Be(before);
        var after = await _db.UsageEvents.AsNoTracking().SingleAsync(Ct);
        after.InputTokens.Should().Be(9007199254740993);
        after.SourceKind.Should().Be(AiObservatory.Data.Entities.SourceKind.Legacy);
        after.CostBasis.Should().Be(CostBasis.Unknown);
        (await _store.RegisterMachineAsync(new byte[32], Ct)).MachineId.Should().NotBeEmpty();
    }

    /// <summary>The capture budget includes its retained manifest as well as its chunk bytes.</summary>
    [Fact]
    public async Task SealCapture_ManifestExceedsCompleteCaptureBudget_RejectsAtomically()
    {
        var lease = await Capture();
        var tiny = new AuditStore(_db, _clock, new() { MaxChunkBytes = 1, MaxCaptureBytes = 1 });
        var action = () => tiny.SealCaptureAsync(lease, Manifest(lease, [], 0), "seal", Ct);
        (await action.Should().ThrowAsync<AuditStoreException>()).Which.StatusCode.Should().Be(413);
        (await _store.ReadCaptureAsync(lease.RequestId, Ct)).Should().BeNull();
    }

    /// <summary>Contradictory facts under one stable identity must never become immutable evidence.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SealCapture_ConflictingStableIdentity_RejectsWithinAndAcrossChunks(bool separateChunks)
    {
        var lease = await Capture();
        var first = Record(lease.MachineId, model: "model-a");
        var second = Record(lease.MachineId, model: "model-b");
        byte[][] chunks = separateChunks
            ? [first, second]
            :
            [
                [.. first, .. second],
            ];
        var hashes = chunks.Select(chunk => AuditCanonicalJson.ContentHash(chunk)).ToArray();
        for (var index = 0; index < chunks.Length; index++)
        {
            await _store.PutChunkAsync(lease, index, chunks[index], hashes[index], Ct);
        }
        var action = () => _store.SealCaptureAsync(lease, Manifest(lease, hashes, 2), "seal", Ct);
        var rejected = (await action.Should().ThrowAsync<AuditStoreException>()).Which;
        rejected.StatusCode.Should().Be(409);
        rejected.Code.Should().Be("capture_record_identity_conflict");
        (await _store.ReadCaptureAsync(lease.RequestId, Ct)).Should().BeNull();
        await _store.PutChunkAsync(lease, 0, chunks[0], hashes[0], Ct);
    }

    /// <summary>Identical repeated original facts remain retained with their original line count and bytes.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SealCapture_IdenticalStableIdentity_PreservesDuplicatesWithinAndAcrossChunks(bool separateChunks)
    {
        var lease = await Capture();
        var record = Record(lease.MachineId, model: "model-a");
        byte[][] chunks = separateChunks
            ? [record, record]
            :
            [
                [.. record, .. record],
            ];
        var hashes = chunks.Select(chunk => AuditCanonicalJson.ContentHash(chunk)).ToArray();
        for (var index = 0; index < chunks.Length; index++)
        {
            await _store.PutChunkAsync(lease, index, chunks[index], hashes[index], Ct);
        }
        var manifest = Manifest(lease, hashes, 2);
        var id = await _store.SealCaptureAsync(lease, manifest, "seal", Ct);
        var retained = (await _store.ReadCaptureAsync(id, Ct))!;
        retained.Manifest.Should().Equal(manifest);
        retained.Chunks.Should().HaveCount(chunks.Length);
        for (var index = 0; index < chunks.Length; index++)
        {
            retained.Chunks[index].Should().Equal(chunks[index]);
        }
        AuditCanonicalJson.Deserialize<CaptureManifest>(retained.Manifest).RecordCount.Should().Be(2);
    }

    /// <summary>Unsupported finite settings cannot poison subsequent seals or run completions.</summary>
    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(3_000_000)]
    [InlineData(36_501)]
    public async Task UpdateRetention_UnsupportedFiniteDays_RejectsWithoutChangingDefaults(int days)
    {
        foreach (var values in new (int? Evidence, int? Summary)[] { (days, null), (null, days) })
        {
            var action = () => _store.UpdateRetentionAsync(values.Evidence, values.Summary, Ct);
            var rejected = (await action.Should().ThrowAsync<AuditStoreException>()).Which;
            rejected.StatusCode.Should().Be(400);
            rejected.Code.Should().Be("invalid_retention");
        }
        var lease = await Capture();
        var captureId = await _store.SealCaptureAsync(lease, Manifest(lease, [], 0), "seal", Ct);
        (await _store.ReadCaptureAsync(captureId, Ct))!.ExpiresAt.Should().Be(Now.Plus(Duration.FromDays(90)));
        var run = await Run();
        var worker = (await _store.TryLeaseRunAsync(Guid.NewGuid(), Now, Ct))!;
        (await PublishFrozen(worker)).Should().BeTrue();
        (await _store.GetRunAsync(run, Ct))!.SummaryExpiresAt.Should().Be(Now.Plus(Duration.FromDays(365)));
    }

    /// <summary>Even a supported finite day count must be rejected if the actual current Instant cannot hold its expiry.</summary>
    [Fact]
    public async Task UpdateRetention_InstantBoundary_RejectsFiniteExpiryAndAllowsIndefinite()
    {
        var clock = new FakeClock(Now);
        var store = new AuditStore(_db, clock);
        clock.Advance(Instant.MaxValue - Now - Duration.FromHours(1));
        foreach (var values in new (int? Evidence, int? Summary)[] { (1, null), (null, 1) })
        {
            var action = () => store.UpdateRetentionAsync(values.Evidence, values.Summary, Ct);
            var rejected = (await action.Should().ThrowAsync<AuditStoreException>()).Which;
            rejected.StatusCode.Should().Be(400);
            rejected.Code.Should().Be("invalid_retention");
        }
        await store.UpdateRetentionAsync(null, null, Ct);
    }

    /// <summary>The documented finite upper bound remains usable rather than rejecting all finite settings.</summary>
    [Fact]
    public async Task UpdateRetention_SupportedMaximum_StoresRepresentableExpiry()
    {
        await _store.UpdateRetentionAsync(36_500, 36_500, Ct);
        var lease = await Capture();
        var captureId = await _store.SealCaptureAsync(lease, Manifest(lease, [], 0), "seal", Ct);
        (await _store.ReadCaptureAsync(captureId, Ct))!.ExpiresAt.Should().Be(Now.Plus(Duration.FromDays(36_500)));
    }

    /// <summary>The exact detailed deadline hides canonical bytes and preserves only a separately identified safe summary.</summary>
    [Fact]
    public async Task Retention_ExactEvidenceAndSummaryBoundaries_RedactThenRemove()
    {
        var id = await Run();
        var worker = (await _store.TryLeaseRunAsync(Guid.NewGuid(), Now, Ct))!;
        (await PublishFrozen(worker)).Should().BeTrue();
        var original = (await _store.GetRunAsync(id, Ct))!;
        _clock.Advance(Duration.FromDays(90) - Duration.FromTicks(1));
        (await _store.GetRunAsync(id, Ct))!.Result.Should().NotBeNull();
        _clock.Advance(Duration.FromTicks(1));
        var expired = (await _store.GetRunAsync(id, Ct))!;
        expired.Result.Should().BeNull();
        expired.SanitizedSummary.Should().NotBeNull();
        expired.OriginalResultHash.Should().Be(AuditCanonicalJson.ContentHash(original.Result!));
        await _store.ExpireAsync(_clock.GetCurrentInstant(), Ct);
        (await _store.GetRunAsync(id, Ct))!.ServerCutoff.Should().Be(original.ServerCutoff);
        _clock.Advance(Duration.FromDays(275) - Duration.FromTicks(1));
        (await _store.GetRunAsync(id, Ct)).Should().NotBeNull();
        _clock.Advance(Duration.FromTicks(1));
        (await _store.GetRunAsync(id, Ct)).Should().BeNull();
        (await _store.ExpireAsync(_clock.GetCurrentInstant(), Ct)).Runs.Should().Be(1);
    }

    /// <summary>Null retention remains indefinite through cleanup and existing retained capture deadlines never move on reuse.</summary>
    [Fact]
    public async Task Retention_IndefiniteAndReuse_KeepOriginalDeadlines()
    {
        var capture = await Capture();
        var id = await _store.SealCaptureAsync(capture, Manifest(capture, [], 0), "seal", Ct);
        await _store.UpdateRetentionAsync(null, null, Ct);
        _clock.Advance(Duration.FromDays(1));
        var run = await _store.CreateRunAsync(capture.Scope, [id], "reuse", false, _clock.GetCurrentInstant(), Ct);
        (await _store.GetRunAsync(run, Ct))!.EvidenceExpiresAt.Should().Be(Now.Plus(Duration.FromDays(90)));
        var reusedWorker = (await _store.TryLeaseRunAsync(Guid.NewGuid(), _clock.GetCurrentInstant(), Ct))!;
        (await PublishFrozen(reusedWorker)).Should().BeTrue();
        (await _store.GetRunAsync(run, Ct))!.EvidenceExpiresAt.Should().Be(Now.Plus(Duration.FromDays(90)));
        var independent = await Run();
        var worker = (await _store.TryLeaseRunAsync(Guid.NewGuid(), _clock.GetCurrentInstant(), Ct))!;
        worker.RunId.Should().Be(independent);
        (await PublishFrozen(worker)).Should().BeTrue();
        _clock.Advance(Duration.FromDays(500));
        await _store.ExpireAsync(_clock.GetCurrentInstant(), Ct);
        (await _store.GetRunAsync(independent, Ct))!.Result.Should().NotBeNull();
    }

    /// <summary>A persistent cancelled export protects chunks until release while new readers and stale workers are fenced.</summary>
    [Fact]
    public async Task Retention_CaptureDeletion_CancelsCrossContextLeaseBeforePurging()
    {
        var capture = await Capture();
        var bytes = Record(capture.MachineId);
        var hash = AuditCanonicalJson.ContentHash(bytes);
        await _store.PutChunkAsync(capture, 0, bytes, hash, Ct);
        var id = await _store.SealCaptureAsync(capture, Manifest(capture, [hash], 1), "seal", Ct);
        var first = await _store.CreateRunAsync(capture.Scope, [id], "first", false, Now, Ct);
        var worker = (await _store.TryLeaseRunAsync(Guid.NewGuid(), Now, Ct))!;
        (await PublishFrozen(worker)).Should().BeTrue();
        var export = await _store.AcquireExportLeaseAsync(first, Ct);
        var impact = await _store.GetDeletionImpactAsync(id, Ct);
        var second = await _store.CreateRunAsync(capture.Scope, [id], "second", false, Now, Ct);
        var stale = () => _store.FenceCaptureDeletionAsync(id, impact.ImpactDigest, Ct);
        (await stale.Should().ThrowAsync<AuditStoreException>()).Which.StatusCode.Should().Be(412);
        worker = (await _store.TryLeaseRunAsync(Guid.NewGuid(), Now, Ct))!;
        impact = await _store.GetDeletionImpactAsync(id, Ct);
        impact.AffectedRunIds.Should().BeEquivalentTo([first, second]);
        await using var otherDb = new AiObservatoryDbContext(_options);
        var other = new AuditStore(otherDb, _clock);
        await other.FenceCaptureDeletionAsync(id, impact.ImpactDigest, Ct);
        (await _store.ExportLeaseValidAsync(export.Id, Ct)).Should().BeFalse();
        (await _store.TryPublishAsync(worker, Result(capture.MachineId), Ct)).Should().BeFalse();
        var unavailable = () => _store.AcquireExportLeaseAsync(first, Ct);
        (await unavailable.Should().ThrowAsync<AuditStoreException>()).Which.StatusCode.Should().Be(410);
        (await _store.PurgeUnavailableCaptureAsync(id, Ct)).Should().BeFalse();
        (
            await _db
                .Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM \"AuditEvidenceChunks\"")
                .SingleAsync(Ct)
        )
            .Should()
            .Be(1);
        await _store.ReleaseExportLeaseAsync(export.Id, Ct);
        (await other.PurgeUnavailableCaptureAsync(id, Ct)).Should().BeTrue();
        (
            await _db
                .Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM \"AuditEvidenceChunks\"")
                .SingleAsync(Ct)
        )
            .Should()
            .Be(0);
    }

    /// <summary>Two bounded reservations reject rather than queue a third export, and the hard deadline is exact.</summary>
    [Fact]
    public async Task Retention_ExportCapacityAndDeadline_AreBounded()
    {
        var id = await Run();
        (await PublishFrozen((await _store.TryLeaseRunAsync(Guid.NewGuid(), Now, Ct))!)).Should().BeTrue();
        var first = await _store.AcquireExportLeaseAsync(id, Ct);
        await _store.AcquireExportLeaseAsync(id, Ct);
        var third = () => _store.AcquireExportLeaseAsync(id, Ct);
        (await third.Should().ThrowAsync<AuditStoreException>()).Which.StatusCode.Should().Be(429);
        _clock.Advance(Duration.FromMinutes(2) - Duration.FromTicks(1));
        (await _store.ExportLeaseValidAsync(first.Id, Ct)).Should().BeTrue();
        _clock.Advance(Duration.FromTicks(1));
        (await _store.ExportLeaseValidAsync(first.Id, Ct)).Should().BeFalse();
        (await _store.AcquireExportLeaseAsync(id, Ct)).Should().NotBeNull();
    }

    /// <summary>Removing one report never destroys a shared capture; removing its last reference does.</summary>
    [Fact]
    public async Task Retention_DeleteRun_RemovesOnlyUnreferencedChunks()
    {
        var capture = await Capture();
        var bytes = Record(capture.MachineId);
        var hash = AuditCanonicalJson.ContentHash(bytes);
        await _store.PutChunkAsync(capture, 0, bytes, hash, Ct);
        var id = await _store.SealCaptureAsync(capture, Manifest(capture, [hash], 1), "seal", Ct);
        var first = await _store.CreateRunAsync(capture.Scope, [id], "one", false, Now, Ct);
        var second = await _store.CreateRunAsync(capture.Scope, [id], "two", false, Now, Ct);
        (await _store.DeleteRunAsync(first, Ct)).Should().BeTrue();
        (await _store.ReadCaptureAsync(id, Ct))!.Chunks.Should().ContainSingle();
        (await _store.GetRunAsync(second, Ct)).Should().NotBeNull();
        (await _store.DeleteRunAsync(second, Ct)).Should().BeTrue();
        (await _store.ReadCaptureAsync(id, Ct)).Should().BeNull();
    }

    /// <summary>Indefinite captures assigned before readiness pinning must be erased only after their last run reference disappears.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retention_DeleteCollectingRun_CleansAssignedIndefiniteCaptureAndPreservesSharedReferences(
        bool shared
    )
    {
        await _store.UpdateRetentionAsync(null, null, Ct);
        var machine = await Machine();
        var scope = Scope(machine.MachineId);
        var collecting = await _store.CreateRunAsync(scope, [], "collecting", true, Now, Ct);
        var lease = (await _store.TryClaimCaptureAsync(machine.MachineId, machine.CredentialId, Now, Ct))!;
        var bytes = Record(machine.MachineId);
        var hash = AuditCanonicalJson.ContentHash(bytes);
        await _store.PutChunkAsync(lease, 0, bytes, hash, Ct);
        var capture = await _store.SealCaptureAsync(lease, Manifest(lease, [hash], 1), "seal", Ct);
        (await _store.ReadCaptureAsync(capture, Ct))!.ExpiresAt.Should().BeNull();
        (
            await _db
                .Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM \"AuditRunCaptures\"")
                .SingleAsync(Ct)
        )
            .Should()
            .Be(0);
        Guid? otherRun = shared ? await _store.CreateRunAsync(scope, [capture], "shared", false, Now, Ct) : null;
        (await _store.DeleteRunAsync(collecting, Ct)).Should().BeTrue();
        (await _store.GetRunAsync(collecting, Ct)).Should().BeNull();
        if (otherRun is { } remaining)
        {
            var retained = (await _store.ReadCaptureAsync(capture, Ct))!;
            retained.Chunks.Should().ContainSingle().Which.Should().Equal(bytes);
            retained.Manifest.Should().Equal(Manifest(lease, [hash], 1));
            (await _store.GetRunAsync(remaining, Ct)).Should().NotBeNull();
            (await _store.DeleteRunAsync(remaining, Ct)).Should().BeTrue();
        }
        (await _store.ReadCaptureAsync(capture, Ct)).Should().BeNull();
        (
            await _db
                .Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM \"AuditEvidenceChunks\"")
                .SingleAsync(Ct)
        )
            .Should()
            .Be(0);
        (
            await _db
                .Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM \"AuditEvidenceCaptures\"")
                .SingleAsync(Ct)
        )
            .Should()
            .Be(0);
    }

    /// <summary>Abandoned bytes expire exactly at the request's 24-hour boundary and cannot be sealed afterwards.</summary>
    [Fact]
    public async Task Retention_AbandonedUpload_Exact24HourBoundary()
    {
        var capture = await Capture();
        var bytes = Record(capture.MachineId);
        await _store.PutChunkAsync(capture, 0, bytes, AuditCanonicalJson.ContentHash(bytes), Ct);
        _clock.Advance(Duration.FromHours(24) - Duration.FromTicks(1));
        (await _store.ExpireAsync(_clock.GetCurrentInstant(), Ct)).Uploads.Should().Be(0);
        _clock.Advance(Duration.FromTicks(1));
        (await _store.ExpireAsync(_clock.GetCurrentInstant(), Ct)).Uploads.Should().Be(1);
        var attempt = () => _store.SealCaptureAsync(capture, Manifest(capture, [], 0), "late", Ct);
        (await attempt.Should().ThrowAsync<AuditStoreException>()).Which.StatusCode.Should().Be(409);
        (
            await _db
                .Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM \"AuditEvidenceChunks\"")
                .SingleAsync(Ct)
        )
            .Should()
            .Be(0);
    }

    /// <summary>Assigned collecting captures belong in impact even before readiness pins their links.</summary>
    [Fact]
    public async Task Retention_CollectingAssignment_IsIncludedInDeletionImpact()
    {
        var machine = await Machine();
        var run = await _store.CreateRunAsync(Scope(machine.MachineId), [], "collect", true, Now, Ct);
        var capture = (await _store.TryClaimCaptureAsync(machine.MachineId, machine.CredentialId, Now, Ct))!;
        var id = await _store.SealCaptureAsync(capture, Manifest(capture, [], 0), "seal", Ct);
        var impact = await _store.GetDeletionImpactAsync(id, Ct);
        impact.AffectedRunIds.Should().Contain(run);
        await _store.DeleteCaptureAsync(id, impact.ImpactDigest, Ct);
        (await _store.GetRunAsync(run, Ct))!.State.Should().Be("failed");
        await _store.RefreshCollectingRunsAsync(Ct);
        (await _store.TryLeaseRunAsync(Guid.NewGuid(), Now, Ct)).Should().BeNull();
    }

    /// <summary>Expired report storage drops all detailed references and price terms while ordinary accounting rows are byte-for-byte untouched.</summary>
    [Fact]
    public async Task Retention_Expiry_RemovesSensitiveDetailsAndPreservesOrdinaryLedgers()
    {
        var usage = new UsageEvent
        {
            Provider = Provider.Anthropic,
            OccurredAt = Now,
            IngestedAt = Now,
            InputTokens = 9007199254740993,
            CostUsd = 123.456m,
            EventKey = "ordinary-event",
        };
        var category = new SpendCategory
        {
            Key = "synthetic",
            DisplayName = "Synthetic",
            ColorVar = "--accent",
        };
        var vendor = new SpendVendor { Key = "synthetic", DisplayName = "Synthetic" };
        _db.AddRange(usage, category, vendor);
        _db.Add(
            new SpendEntry
            {
                VendorId = vendor.Id,
                CategoryId = category.Id,
                OccurredOn = new(2026, 10, 22),
                Amount = 3.45m,
                AmountGbp = 3.45m,
                FxRate = 1,
                RecordedAt = Now,
            }
        );
        _db.Add(
            new AiObservatory.Data.Audit.PricingApplication
            {
                EventId = usage.Id,
                Revision = 1,
                RevisionDigest = new string('c', 64),
                Kind = "synthetic",
                PricingDate = new(2026, 10, 22),
                CostUsd = 123.456m,
            }
        );
        await _db.SaveChangesAsync(Ct);
        var ordinary = await OrdinaryRows();
        var run = await Run();
        var lease = (await _store.TryLeaseRunAsync(Guid.NewGuid(), Now, Ct))!;
        var input = new AuditInput
        {
            SchemaVersion = 1,
            VerifierVersion = "1",
            RuleVersion = "1",
            Scope = Scope(),
        };
        await _store.TryStoreSnapshotAsync(
            lease,
            AuditCanonicalJson.Serialize(input),
            AuditComputationInput.ContentDigest(input),
            Ct
        );
        var finding = new AuditFinding(
            CheckCode.PricingArithmetic,
            "synthetic",
            "private-group",
            ["private-record"],
            "costUsd",
            new(AuditValueKind.Decimal, "987.654"),
            null,
            null,
            ValueUnit.Money,
            "USD",
            AccountingBasis.Notional,
            "synthetic"
        );
        var result = Result() with
        {
            InputContentDigest = AuditComputationInput.ContentDigest(input),
            Findings = [finding],
            Checks =
            [
                new(
                    CheckCode.SourceAccounting,
                    CheckVerdict.Incomplete,
                    new(1, 2, [new("source", "private-record", "synthetic")]),
                    [finding]
                )
                {
                    PricingTraces =
                    [
                        new(
                            "private-record",
                            "private-catalog",
                            "private-rate",
                            PriceMeter.Input,
                            17,
                            1000,
                            987.654m,
                            1,
                            3,
                            "USD"
                        ),
                    ],
                },
            ],
        };
        (await _store.TryPublishAsync(lease, result, Ct)).Should().BeTrue();
        _clock.Advance(Duration.FromDays(90));
        await _store.ExpireAsync(_clock.GetCurrentInstant(), Ct);
        var view = (await _store.GetRunAsync(run, Ct))!;
        var summary = System.Text.Encoding.UTF8.GetString(view.SanitizedSummary!);
        summary
            .Should()
            .NotContain("private-record")
            .And.NotContain("private-group")
            .And.NotContain("private-catalog")
            .And.NotContain("987.654");
        view.OriginalResultHash.Should().Be(AuditCanonicalJson.ContentHash(AuditCanonicalJson.Serialize(result)));
        (
            await _db
                .Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM \"AuditFindingRecords\"")
                .SingleAsync(Ct)
        )
            .Should()
            .Be(0);
        (await OrdinaryRows()).Should().Equal(ordinary);
    }

    private async Task<string[]> OrdinaryRows()
    {
        return await _db
            .Database.SqlQueryRaw<string>(
                "SELECT to_jsonb(t)::text AS \"Value\" FROM \"UsageEvents\" t UNION ALL SELECT to_jsonb(t)::text FROM \"SpendEntries\" t UNION ALL SELECT to_jsonb(t)::text FROM \"PricingApplications\" t ORDER BY \"Value\""
            )
            .ToArrayAsync(Ct);
    }

    /// <summary>An oversized cohort cannot allocate an evidence body or leave a reservation consuming capacity.</summary>
    [Fact]
    public async Task Retention_ExportOversize_RejectsBeforeMaterializationAndReservation()
    {
        var run = await Run();
        (await PublishFrozen((await _store.TryLeaseRunAsync(Guid.NewGuid(), Now, Ct))!)).Should().BeTrue();
        var lowered = new AuditStore(_db, _clock, new() { MaxSnapshotBytes = 1 });
        var acquire = () => lowered.AcquireExportLeaseAsync(run, Ct);
        (await acquire.Should().ThrowAsync<AuditStoreException>()).Which.StatusCode.Should().Be(413);
        var materialize = () => lowered.MaterializeExportAsync(run, Ct);
        (await materialize.Should().ThrowAsync<AuditStoreException>()).Which.StatusCode.Should().Be(413);
        (
            await _db
                .Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM \"AuditExportReadLeases\"")
                .SingleAsync(Ct)
        )
            .Should()
            .Be(0);
    }

    private async Task<bool> PublishFrozen(RunLease lease)
    {
        var materialization = await _store.ReadSnapshotMaterializationAsync(lease, Ct);
        var input = new AuditInput
        {
            SchemaVersion = 1,
            VerifierVersion = "1",
            RuleVersion = "1",
            Scope = materialization.Scope,
        };
        var bytes = AuditCanonicalJson.Serialize(input);
        (await _store.TryStoreSnapshotAsync(lease, bytes, AuditComputationInput.ContentDigest(input), Ct))
            .Should()
            .BeTrue();
        return await _store.TryPublishAsync(lease, new AuditVerifier().Verify(input), Ct);
    }

    private Task<Guid> Run() =>
        _store.CreateRunAsync(Scope(), [], Guid.NewGuid().ToString("N"), false, _clock.GetCurrentInstant(), Ct);

    private Task<MachineRegistration> Machine() => _store.RegisterMachineAsync(new byte[32], Ct);

    private async Task<CaptureLease> Capture()
    {
        var machine = await Machine();
        await _store.CreateCaptureRequestAsync(
            machine.MachineId,
            Scope(machine.MachineId),
            _clock.GetCurrentInstant(),
            Ct
        );
        return (
            await _store.TryClaimCaptureAsync(machine.MachineId, machine.CredentialId, _clock.GetCurrentInstant(), Ct)
        )!;
    }

    private static readonly Guid DefaultMachine = Guid.Parse("c68f47b5-78b1-4c12-aec6-9ea8a188740d");

    private static AuditScope Scope(Guid? machine = null) =>
        new(
            new(2026, 10, 1),
            new(2026, 10, 7),
            [new(machine ?? DefaultMachine, "claude-local")],
            [CheckCode.SourceAccounting],
            [],
            true
        );

    private static AuditResult Result(Guid? machine = null) =>
        new(
            1,
            AuditResultScope.FromScope(Scope(machine)),
            new string('a', 64),
            "1",
            "1",
            [],
            new(0, 0, []),
            [],
            CheckVerdict.Incomplete
        );

    private static byte[] Record(Guid machine, long sequence = 1, string? model = null) =>
        [
            .. AuditCanonicalJson.Serialize(
                new SourceUsageRecord
                {
                    MachineId = machine,
                    SourceId = "claude-local",
                    SourceFormat = "claude-jsonl",
                    SourceVersion = "1",
                    Tool = SourceTool.Claude,
                    Kind = UsageRecordKind.SessionMetadata,
                    Generation = "generation-a",
                    FileId = "file-a",
                    Sequence = sequence,
                    Model = model,
                    Timestamp = Now.ToDateTimeOffset(),
                    Pricing = new(ServiceTier.Unknown, false, null, null),
                    Closure = new(ClosureState.Unknown, null, null),
                }
            ),
            10,
        ];

    private static byte[] Manifest(CaptureLease lease, IReadOnlyList<string> hashes, long count) =>
        AuditCanonicalJson.Serialize(
            new CaptureManifest(
                1,
                lease.RequestId,
                lease.RequestId,
                lease.MachineId,
                "generation-a",
                "1",
                Now.ToDateTimeOffset(),
                Now.ToDateTimeOffset(),
                lease.Scope,
                [new("claude-local", "claude-jsonl", "1")],
                1,
                1,
                0,
                0,
                count,
                null,
                null,
                [],
                hashes,
                Now.ToDateTimeOffset()
            )
        );
}
