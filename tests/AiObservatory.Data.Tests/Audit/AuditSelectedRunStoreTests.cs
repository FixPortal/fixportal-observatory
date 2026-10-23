using AiObservatory.Audit.Serialization;
using AiObservatory.Data.Audit;
using AiObservatory.Data.Entities;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace AiObservatory.Data.Tests.Audit;

public sealed partial class AuditStoreTests
{
    [Fact]
    public async Task SelectedAdmission_AttemptedQueuedRunCannotAcquireFreshAuthority()
    {
        var id = await Run();
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"AuditRuns\" SET \"Attempts\"=1 WHERE \"Id\"={id}",
            Ct
        );
        var binding = await Binding(id);
        var action = () => Admit(binding);
        await action.Should().ThrowAsync<AuditStoreException>();
        (await _store.GetRunAsync(id, Ct))!.Attempts.Should().Be(1);
    }

    [Theory]
    [InlineData(EntityState.Added)]
    [InlineData(EntityState.Modified)]
    [InlineData(EntityState.Deleted)]
    public async Task SelectedAdmission_RefusesDirtyNonAuditContextWithoutDiscardingCallerWork(EntityState state)
    {
        var id = await Run();
        var binding = await Binding(id);
        var usage = new UsageEvent
        {
            SourceId = "selected-synthetic",
            OccurredAt = Now,
            IngestedAt = Now,
        };
        _db.UsageEvents.Add(usage);
        if (state != EntityState.Added)
        {
            await _db.SaveChangesAsync(Ct);
            _db.Entry(usage).State = state;
        }
        var action = () => Admit(binding);
        (await action.Should().ThrowAsync<AuditStoreException>()).Which.Code.Should().Be("selected_dirty_context");
        _db.Entry(usage).State.Should().Be(state);
        await using var observer = new AiObservatoryDbContext(_options);
        (await new AuditStore(observer, _clock).GetRunAsync(id, Ct))!.Attempts.Should().Be(0);
        (await observer.UsageEvents.AnyAsync(e => e.Id == usage.Id, Ct)).Should().Be(state != EntityState.Added);
        _db.Entry(usage).State = EntityState.Detached;
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("fail")]
    [InlineData("expire")]
    [InlineData("complete")]
    public async Task SelectedAdmission_OldAuthorityCannotReadmitAfterAttempt(string outcome)
    {
        var id = await Run();
        var binding = await Binding(id);
        var lease = (await Admit(binding)).Lease!;
        if (outcome == "cancel")
        {
            await _store.CancelRunAsync(id, Ct);
        }
        else if (outcome == "fail")
        {
            await _store.FailRunAsync(lease, "test_failure", Ct);
        }
        else if (outcome == "complete")
        {
            (await PublishFrozen(lease)).Should().BeTrue();
        }
        else
        {
            _clock.Advance(Duration.FromMinutes(6));
        }
        var before = (await _store.GetRunAsync(id, Ct))!;
        await using var restarted = new AiObservatoryDbContext(_options);
        var store = new AuditStore(restarted, _clock);
        var old = () => Admit(binding, store);
        await old.Should().ThrowAsync<AuditStoreException>();
        var freshRevision = () => Admit(binding with { ExpectedRevision = before.Revision }, store);
        await freshRevision.Should().ThrowAsync<AuditStoreException>();
        (await _store.GetRunAsync(id, Ct)).Should().BeEquivalentTo(before);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SelectedAdmission_ExpiredRunMetadataRefusesWithoutWrites(bool evidence)
    {
        var id = await Run();
        if (evidence)
        {
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"AuditRuns\" SET \"EvidenceExpiresAt\"={Now} WHERE \"Id\"={id}",
                Ct
            );
        }
        else
        {
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"AuditRuns\" SET \"SummaryExpiresAt\"={Now} WHERE \"Id\"={id}",
                Ct
            );
        }
        // Summary expiry deliberately hides public run reads; bind known creation facts.
        var binding = new SelectedRunBinding(
            id,
            AuditCanonicalJson.ContentHash(AuditCanonicalJson.Serialize(Scope())),
            0
        );
        var action = () => Admit(binding);
        await action.Should().ThrowAsync<AuditStoreException>();
        (
            await _db
                .Database.SqlQuery<long>(
                    $"SELECT \"Attempts\"::bigint AS \"Value\" FROM \"AuditRuns\" WHERE \"Id\"={id}"
                )
                .SingleAsync(Ct)
        )
            .Should()
            .Be(0);
    }

    private async Task<SelectedRunBinding> Binding(Guid id)
    {
        var run = (await _store.GetRunAsync(id, Ct))!;
        return new(id, AuditCanonicalJson.ContentHash(run.Scope), run.Revision);
    }

    private Task<SelectedRunAdmission> Admit(SelectedRunBinding binding, AuditStore? store = null) =>
        (store ?? _store).TryAdmitSelectedRunAsync(binding, Guid.NewGuid(), Now + Duration.FromHours(1), Ct);

    [Fact]
    public async Task SelectedAdmission_LeasesExactRunWithoutTouchingOlderQueue()
    {
        var older = await Run();
        var selected = await Run();
        var before = (await _store.GetRunAsync(older, Ct))!;
        var binding = await Binding(selected);
        var admission = await Admit(binding);
        admission.Status.Should().Be("admitted");
        admission.Lease!.RunId.Should().Be(selected);
        admission.Lease.Revision.Should().Be(binding.ExpectedRevision + 1);
        (await _store.GetRunAsync(selected, Ct))!.Attempts.Should().Be(1);
        (await _store.GetRunAsync(older, Ct)).Should().BeEquivalentTo(before);
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("revision")]
    [InlineData("instance")]
    [InlineData("deadline")]
    public async Task SelectedAdmission_InvalidAuthorityDoesNotWrite(string kind)
    {
        var id = await Run();
        var before = (await _store.GetRunAsync(id, Ct))!;
        var binding = await Binding(id);
        if (kind == "digest")
        {
            binding = binding with { ScopeDigest = new string('f', 64) };
        }
        if (kind == "revision")
        {
            binding = binding with { ExpectedRevision = binding.ExpectedRevision + 1 };
        }
        var store = kind == "instance" ? new AuditStore(_db, _clock, instanceId: "other") : _store;
        var action = () =>
            store.TryAdmitSelectedRunAsync(
                binding,
                Guid.NewGuid(),
                kind == "deadline" ? Now : Now + Duration.FromHours(1),
                Ct
            );
        await action.Should().ThrowAsync<AuditStoreException>();
        (await _store.GetRunAsync(id, Ct)).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task SelectedAdmission_BusyDoesNotReclaimExhaustedRun()
    {
        var live = await Run();
        await _store.TryLeaseRunAsync(Guid.NewGuid(), Now, Ct);
        var exhausted = await Run();
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"AuditRuns\" SET \"State\"='executing', \"Attempts\"=3, \"LeaseExpiresAt\"={Now - Duration.FromMinutes(1)} WHERE \"Id\"={exhausted}",
            Ct
        );
        var id = await Run();
        var before = (await _store.GetRunAsync(id, Ct))!;
        var old = (await _store.GetRunAsync(exhausted, Ct))!;
        (await Admit(await Binding(id))).Status.Should().Be("busy");
        (await _store.GetRunAsync(id, Ct)).Should().BeEquivalentTo(before);
        (await _store.GetRunAsync(exhausted, Ct)).Should().BeEquivalentTo(old);
        (await _store.GetRunAsync(live, Ct))!.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task SelectedAdmission_PendingIsObservational()
    {
        var machine = await Machine();
        var id = await _store.CreateRunAsync(Scope(machine.MachineId), [], "selected-pending", true, Now, Ct);
        var before = (await _store.GetRunAsync(id, Ct))!;
        (await Admit(await Binding(id))).Status.Should().Be("pending");
        (await _store.GetRunAsync(id, Ct)).Should().BeEquivalentTo(before);
        (
            await _db
                .Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM \"AuditRetentionSettings\"")
                .SingleAsync(Ct)
        )
            .Should()
            .Be(0);
    }

    [Fact]
    public async Task SelectedAdmission_ConcurrentAuthorityConsumedOnlyOnce()
    {
        var id = await Run();
        var binding = await Binding(id);
        await using var other = new AiObservatoryDbContext(_options);
        async Task<string> Attempt(AuditStore store)
        {
            try
            {
                return (await Admit(binding, store)).Status;
            }
            catch (AuditStoreException)
            {
                return "conflict";
            }
        }
        var results = await Task.WhenAll(Attempt(_store), Attempt(new AuditStore(other, _clock)));
        results.Should().BeEquivalentTo("admitted", "conflict");
        (await _store.GetRunAsync(id, Ct))!.Attempts.Should().Be(1);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SelectedAdmission_CeilingReservesOneFinalRevision(bool fail, bool terminal)
    {
        var id = await Run();
        var revision = long.MaxValue - 2;
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"AuditRuns\" SET \"Revision\"={revision} WHERE \"Id\"={id}",
            Ct
        );
        var binding = await Binding(id);
        var lease = (await Admit(binding)).Lease!;
        if (fail)
        {
            await _store.FailRunAsync(lease, "test_failure", Ct, terminal: terminal);
        }
        else
        {
            (await PublishFrozen(lease)).Should().BeTrue();
        }
        (await _store.GetRunAsync(id, Ct))!.Revision.Should().Be(long.MaxValue);
        var repeat = () => Admit(binding);
        await repeat.Should().ThrowAsync<AuditStoreException>();
    }

    [Fact]
    public async Task SelectedAdmission_RejectsInsufficientRevisionHeadroom()
    {
        var id = await Run();
        var revision = long.MaxValue - 1;
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"AuditRuns\" SET \"Revision\"={revision} WHERE \"Id\"={id}",
            Ct
        );
        var binding = await Binding(id);
        var action = () => Admit(binding);
        await action.Should().ThrowAsync<AuditStoreException>();
        (await _store.GetRunAsync(id, Ct))!.Attempts.Should().Be(0);
    }
}
