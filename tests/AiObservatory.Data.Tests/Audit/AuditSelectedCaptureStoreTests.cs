using System.Data.Common;
using AiObservatory.Audit.Serialization;
using AiObservatory.Data.Audit;
using AiObservatory.Data.Entities;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NodaTime;

namespace AiObservatory.Data.Tests.Audit;

public sealed partial class AuditStoreTests
{
    private sealed record CaptureSelection(SelectedRunBinding Binding, Guid Request, MachineRegistration Machine);

    private async Task<CaptureSelection> SelectedCapture()
    {
        var machine = await Machine();
        var id = await _store.CreateRunAsync(Scope(machine.MachineId), [], Guid.NewGuid().ToString("N"), true, Now, Ct);
        var request = await _db
            .Database.SqlQuery<Guid>(
                $"SELECT \"Id\" AS \"Value\" FROM \"AuditCaptureRequests\" WHERE \"MachineId\"={machine.MachineId}"
            )
            .SingleAsync(Ct);
        return new(await Binding(id), request, machine);
    }

    private Task<SelectedCaptureLease> ExactClaim(
        CaptureSelection s,
        AuditStore? store = null,
        Instant? expiry = null
    ) =>
        (store ?? _store).ClaimSelectedCaptureAsync(
            s.Binding,
            s.Request,
            s.Machine.MachineId,
            s.Machine.CredentialId,
            AuditCanonicalJson.ContentHash(AuditCanonicalJson.Serialize(Scope(s.Machine.MachineId))),
            expiry ?? Now + Duration.FromHours(1),
            Ct
        );

    private Task<SelectedCaptureLease> ExactRenew(CaptureSelection s, Guid token, AuditStore? store = null) =>
        (store ?? _store).RenewSelectedCaptureAsync(
            s.Binding,
            s.Request,
            s.Machine.MachineId,
            s.Machine.CredentialId,
            token,
            AuditCanonicalJson.ContentHash(AuditCanonicalJson.Serialize(Scope(s.Machine.MachineId))),
            Now + Duration.FromHours(1),
            Ct
        );

    private async Task<string> CaptureFacts() =>
        string.Join(
            "\n",
            await _db
                .Database.SqlQueryRaw<string>(
                    """
                    SELECT row_to_json(t)::text AS "Value" FROM (
                        SELECT 'request' AS kind, row_to_json(r)::text AS data FROM "AuditCaptureRequests" r
                        UNION ALL SELECT 'chunk', row_to_json(c)::text FROM "AuditEvidenceChunks" c
                        UNION ALL SELECT 'capture', row_to_json(c)::text FROM "AuditEvidenceCaptures" c
                        UNION ALL SELECT 'run', row_to_json(r)::text FROM "AuditRuns" r
                    ) t ORDER BY kind, data
                    """
                )
                .ToArrayAsync(Ct)
        );

    [Fact]
    public async Task SelectedCapture_ClaimsOnlyExactFreshRequestAndCapsAuthority()
    {
        var selected = await SelectedCapture();
        var older = await _store.CreateCaptureRequestAsync(
            selected.Machine.MachineId,
            Scope(selected.Machine.MachineId),
            Now - Duration.FromMinutes(1),
            Ct
        );
        var oldToken = Guid.NewGuid();
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"AuditCaptureRequests\" SET \"State\"='uploading',\"CredentialId\"={selected.Machine.CredentialId},\"LeaseToken\"={oldToken},\"LeaseExpiresAt\"={Now},\"UploadedBytes\"=3,\"RecordCount\"=1 WHERE \"Id\"={older}",
            Ct
        );
        await SeedChunk(older);
        var beforeRun = await _store.GetRunAsync(selected.Binding.RunId, Ct);
        var unrelated = await RequestFacts(older);
        var lease = await ExactClaim(selected, expiry: Now + Duration.FromMinutes(2));
        lease.RequestId.Should().Be(selected.Request);
        lease.RunId.Should().Be(selected.Binding.RunId);
        lease.ExpiresAt.Should().Be(Now + Duration.FromMinutes(2));
        lease.ServerObservedAt.Should().Be(Now);
        lease.Token.Should().NotBe(oldToken);
        (await _store.GetRunAsync(selected.Binding.RunId, Ct)).Should().BeEquivalentTo(beforeRun);
        (await RequestFacts(older)).Should().Be(unrelated);
        (await ChunkBytes()).Should().Equal(1, 2, 3);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("request")]
    [InlineData("machine")]
    [InlineData("credential")]
    [InlineData("revision")]
    [InlineData("digest")]
    [InlineData("instance")]
    [InlineData("expiry")]
    [InlineData("subscope")]
    [InlineData("revoked")]
    [InlineData("attempted")]
    [InlineData("foreignRequest")]
    [InlineData("counter")]
    [InlineData("records")]
    [InlineData("owner")]
    [InlineData("token")]
    [InlineData("lease")]
    [InlineData("chunk")]
    [InlineData("capture")]
    [InlineData("requestRevision")]
    public async Task SelectedCapture_RefusalsAreWriteFree(string kind)
    {
        var s = await SelectedCapture();
        await DirtySelectedFixture(s, kind);
        if (kind == "foreignRequest")
        {
            s = s with
            {
                Request = await _store.CreateCaptureRequestAsync(
                    s.Machine.MachineId,
                    Scope(s.Machine.MachineId),
                    Now,
                    Ct
                ),
            };
        }
        s = kind switch
        {
            "run" => s with { Binding = s.Binding with { RunId = Guid.NewGuid() } },
            "request" => s with { Request = Guid.NewGuid() },
            "machine" => s with { Machine = s.Machine with { MachineId = Guid.NewGuid() } },
            "credential" => s with { Machine = s.Machine with { CredentialId = Guid.NewGuid() } },
            "revision" => s with { Binding = s.Binding with { ExpectedRevision = 1 } },
            "digest" => s with { Binding = s.Binding with { ScopeDigest = new string('f', 64) } },
            _ => s,
        };
        var before = await CaptureFacts();
        var action = () =>
            ExactClaim(
                s,
                kind == "instance" ? new AuditStore(_db, _clock, instanceId: "foreign") : null,
                kind == "expiry" ? Now : null
            );
        await action.Should().ThrowAsync<AuditStoreException>();
        (await CaptureFacts()).Should().Be(before);
    }

    private async Task DirtySelectedFixture(CaptureSelection s, string kind)
    {
        if (kind == "subscope")
        {
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"AuditCaptureRequests\" SET \"Scope\"={AuditCanonicalJson.Serialize(Scope())} WHERE \"Id\"={s.Request}",
                Ct
            );
        }
        if (kind == "revoked")
        {
            await _store.RevokeCredentialAsync(s.Machine.CredentialId, Ct);
        }
        if (kind == "attempted")
        {
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"AuditRuns\" SET \"Attempts\"=1 WHERE \"Id\"={s.Binding.RunId}",
                Ct
            );
        }
        if (kind == "counter")
        {
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"AuditCaptureRequests\" SET \"UploadedBytes\"=1 WHERE \"Id\"={s.Request}",
                Ct
            );
        }
        if (kind == "records")
        {
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"AuditCaptureRequests\" SET \"RecordCount\"=1 WHERE \"Id\"={s.Request}",
                Ct
            );
        }
        if (kind == "owner")
        {
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"AuditCaptureRequests\" SET \"CredentialId\"={s.Machine.CredentialId} WHERE \"Id\"={s.Request}",
                Ct
            );
        }
        if (kind == "token")
        {
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"AuditCaptureRequests\" SET \"LeaseToken\"={Guid.NewGuid()} WHERE \"Id\"={s.Request}",
                Ct
            );
        }
        if (kind == "lease")
        {
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"AuditCaptureRequests\" SET \"LeaseExpiresAt\"={Now} WHERE \"Id\"={s.Request}",
                Ct
            );
        }
        if (kind == "requestRevision")
        {
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"AuditCaptureRequests\" SET \"Revision\"=1 WHERE \"Id\"={s.Request}",
                Ct
            );
        }
        if (kind == "chunk")
        {
            await SeedChunk(s.Request);
        }
        if (kind == "capture")
        {
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO \"AuditEvidenceCaptures\" (\"Id\",\"RequestId\",\"MachineId\",\"Generation\",\"Manifest\",\"ManifestHash\",\"SealKey\",\"SealedAt\",\"Revision\") VALUES ({Guid.NewGuid()},{s.Request},{s.Machine.MachineId},{"synthetic"},{new byte[] { 1 }},{new string('a', 64)},{"synthetic"},{Now},0)",
                Ct
            );
        }
    }

    private async Task SeedChunk(Guid request)
    {
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO \"AuditEvidenceChunks\" (\"RequestId\",\"Index\",\"Bytes\",\"RecordCount\",\"Sha256\") VALUES ({request},0,{new byte[] { 1, 2, 3 }},1,{new string('a', 64)})",
            Ct
        );
    }

    private Task<string> RequestFacts(Guid id) =>
        _db
            .Database.SqlQuery<string>(
                $"SELECT row_to_json(r)::text AS \"Value\" FROM \"AuditCaptureRequests\" r WHERE \"Id\"={id}"
            )
            .SingleAsync(Ct);

    private Task<byte[]> ChunkBytes() =>
        _db.Database.SqlQueryRaw<byte[]>("SELECT \"Bytes\" AS \"Value\" FROM \"AuditEvidenceChunks\"").SingleAsync(Ct);

    [Fact]
    public async Task SelectedCapture_ConcurrentClaimHasOneWinner()
    {
        var s = await SelectedCapture();
        await using var second = new AiObservatoryDbContext(_options);
        async Task<bool> Attempt(AuditStore store)
        {
            try
            {
                await ExactClaim(s, store);
                return true;
            }
            catch (AuditStoreException)
            {
                return false;
            }
        }
        (await Task.WhenAll(Attempt(_store), Attempt(new AuditStore(second, _clock)))).Count(x => x).Should().Be(1);
    }

    [Fact]
    public async Task SelectedCapture_RenewPreservesBytesAndNeverReclaimsExpiredOrWrongOwner()
    {
        var s = await SelectedCapture();
        var lease = await ExactClaim(s);
        await SeedChunk(s.Request);
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"AuditCaptureRequests\" SET \"UploadedBytes\"=3,\"RecordCount\"=1 WHERE \"Id\"={s.Request}",
            Ct
        );
        _clock.Advance(Duration.FromMinutes(1));
        var renewed = await ExactRenew(s, lease.Token);
        renewed.Token.Should().Be(lease.Token);
        renewed.ExpiresAt.Should().BeGreaterThan(lease.ExpiresAt);
        (await ChunkBytes()).Should().Equal(1, 2, 3);
        (
            await _db
                .Database.SqlQueryRaw<long>("SELECT \"UploadedBytes\" AS \"Value\" FROM \"AuditCaptureRequests\"")
                .SingleAsync(Ct)
        )
            .Should()
            .Be(3);
        (
            await _db
                .Database.SqlQueryRaw<long>("SELECT \"RecordCount\" AS \"Value\" FROM \"AuditCaptureRequests\"")
                .SingleAsync(Ct)
        )
            .Should()
            .Be(1);
        var before = await CaptureFacts();
        var wrong = () => ExactRenew(s, Guid.NewGuid());
        await wrong.Should().ThrowAsync<AuditStoreException>();
        (await CaptureFacts()).Should().Be(before);
        _clock.Advance(Duration.FromMinutes(6));
        var expired = () => ExactRenew(s, lease.Token);
        await expired.Should().ThrowAsync<AuditStoreException>();
        var reclaim = () => ExactClaim(s);
        await reclaim.Should().ThrowAsync<AuditStoreException>();
        (await CaptureFacts()).Should().Be(before);
    }

    [Theory]
    [InlineData(EntityState.Added)]
    [InlineData(EntityState.Modified)]
    [InlineData(EntityState.Deleted)]
    public async Task SelectedCapture_DirtyTrackerIsPreservedBeforeTransactionReset(EntityState state)
    {
        var s = await SelectedCapture();
        var usage = new UsageEvent
        {
            SourceId = "synthetic",
            OccurredAt = Now,
            IngestedAt = Now,
        };
        _db.Add(usage);
        if (state != EntityState.Added)
        {
            await _db.SaveChangesAsync(Ct);
            _db.Entry(usage).State = state;
        }
        var before = await CaptureFacts();
        var action = () => ExactClaim(s);
        (await action.Should().ThrowAsync<AuditStoreException>()).Which.Code.Should().Be("selected_dirty_context");
        _db.Entry(usage).State.Should().Be(state);
        (await CaptureFacts()).Should().Be(before);
        _db.Entry(usage).State = EntityState.Detached;
    }

    [Fact]
    public async Task SelectedCapture_RenewalCannotTransferToRotatedCredential()
    {
        var s = await SelectedCapture();
        var lease = await ExactClaim(s);
        await SeedChunk(s.Request);
        var rotated = await _store.RotateMachineCredentialAsync(s.Machine.MachineId, new byte[32], Ct);
        var before = await CaptureFacts();
        var revoked = () => ExactRenew(s, lease.Token);
        (await revoked.Should().ThrowAsync<AuditStoreException>()).Which.Code.Should().Be("credential_revoked");
        var transfer = () => ExactRenew(s with { Machine = rotated }, lease.Token);
        (await transfer.Should().ThrowAsync<AuditStoreException>()).Which.Code.Should().Be("capture_lease_conflict");
        (await CaptureFacts()).Should().Be(before);
    }

    [Fact]
    public async Task SelectedCapture_LockWaitCrossingAuthorityExpiryWritesNothing()
    {
        var s = await SelectedCapture();
        var before = await CaptureFacts();
        await using var blocker = new AiObservatoryDbContext(_options);
        await using var transaction = await blocker.Database.BeginTransactionAsync(Ct);
        await blocker.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({AuditStore.EvidenceLockKey})",
            Ct
        );
        var claim = ExactClaim(s, expiry: Now + Duration.FromSeconds(1));
        await using var monitor = new AiObservatoryDbContext(_options);
        await PostgresLockWait.WaitForBlockedStatementAsync(monitor, "pg_advisory_xact_lock", Ct);
        _clock.Advance(Duration.FromSeconds(2));
        await transaction.CommitAsync(Ct);
        var action = () => claim;
        await action.Should().ThrowAsync<AuditStoreException>();
        (await CaptureFacts()).Should().Be(before);
    }

    private sealed class CaptureCommitFault(Action action) : DbTransactionInterceptor
    {
        private bool _fired;

        public override Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default
        )
        {
            if (!_fired)
            {
                _fired = true;
                action();
                throw new IOException("synthetic commit uncertainty");
            }
            return Task.CompletedTask;
        }
    }

    private sealed class CaptureReadClock(Action advance) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default
        )
        {
            if (command.CommandText.Contains("FROM \"AuditEvidenceChunks\"", StringComparison.Ordinal))
            {
                advance();
            }
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task SelectedCapture_ReadCrossingAuthorityExpiryWritesNothing()
    {
        var s = await SelectedCapture();
        var before = await CaptureFacts();
        var read = new CaptureReadClock(() => _clock.Advance(Duration.FromSeconds(2)));
        await using var db = new AiObservatoryDbContext(
            new DbContextOptionsBuilder<AiObservatoryDbContext>(_options).AddInterceptors(read).Options
        );
        var store = new AuditStore(db, _clock);
        var action = () => ExactClaim(s, store, Now + Duration.FromSeconds(1));
        await action.Should().ThrowAsync<AuditStoreException>();
        (await CaptureFacts()).Should().Be(before);
    }

    private sealed class CaptureCommitClock(Action advance) : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default
        )
        {
            advance();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task SelectedCapture_ObservedTimeIsSampledAfterKnownCommit()
    {
        var s = await SelectedCapture();
        var commit = new CaptureCommitClock(() => _clock.Advance(Duration.FromSeconds(2)));
        await using var db = new AiObservatoryDbContext(
            new DbContextOptionsBuilder<AiObservatoryDbContext>(_options).AddInterceptors(commit).Options
        );
        var lease = await ExactClaim(s, new AuditStore(db, _clock));
        lease.ServerObservedAt.Should().Be(Now + Duration.FromSeconds(2));
        lease.ExpiresAt.Should().Be(Now + Duration.FromMinutes(5));
    }

    [Theory]
    [InlineData("request")]
    [InlineData("evidence")]
    [InlineData("summary")]
    public async Task SelectedCapture_LeaseNeverOutlivesSelectedEvidenceOrRequest(string boundary)
    {
        var s = await SelectedCapture();
        var expiry = Now + Duration.FromSeconds(30);
        switch (boundary)
        {
            case "request":
                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE \"AuditCaptureRequests\" SET \"Deadline\"={expiry} WHERE \"Id\"={s.Request}",
                    Ct
                );
                break;
            case "evidence":
                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE \"AuditRuns\" SET \"EvidenceExpiresAt\"={expiry} WHERE \"Id\"={s.Binding.RunId}",
                    Ct
                );
                break;
            default:
                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE \"AuditRuns\" SET \"SummaryExpiresAt\"={expiry} WHERE \"Id\"={s.Binding.RunId}",
                    Ct
                );
                break;
        }
        (await ExactClaim(s)).ExpiresAt.Should().Be(expiry);
    }

    [Fact]
    public async Task SelectedCapture_PostCommitUncertaintyNeverReturnsSuccessOrRetries()
    {
        var s = await SelectedCapture();
        var fault = new CaptureCommitFault(() => _clock.Advance(Duration.FromSeconds(1)));
        await using var db = new AiObservatoryDbContext(
            new DbContextOptionsBuilder<AiObservatoryDbContext>(_options).AddInterceptors(fault).Options
        );
        var store = new AuditStore(db, _clock);
        var action = () => ExactClaim(s, store);
        await action.Should().ThrowAsync<IOException>();
        (
            await _db
                .Database.SqlQueryRaw<long>("SELECT \"Revision\" AS \"Value\" FROM \"AuditCaptureRequests\"")
                .SingleAsync(Ct)
        )
            .Should()
            .Be(1);
        var repeat = () => ExactClaim(s, store);
        await repeat.Should().ThrowAsync<AuditStoreException>();
        // The fault is one-shot: a later independent exact synthetic request can succeed.
        var control = await SelectedCapture();
        (await ExactClaim(control, store)).ServerObservedAt.Should().Be(_clock.GetCurrentInstant());
    }
}
