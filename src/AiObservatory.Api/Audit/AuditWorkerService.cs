using AiObservatory.Audit;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using AiObservatory.Data;
using AiObservatory.Data.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;

namespace AiObservatory.Api.Audit;

/// <summary>One lease-bound unit of durable work. No verifier work holds the evidence lock.</summary>
public sealed class AuditWorkerService(
    AiObservatoryDbContext db,
    AuditStore store,
    IClock clock,
    IOptions<AuditOptions> options,
    DbContextOptions<AiObservatoryDbContext> dbOptions,
    IAuditRunVerifier? verifier = null
)
{
    private readonly IAuditRunVerifier _verifier = verifier ?? new AuditRunVerifier();

    public async Task<bool> ExecuteOneAsync(Guid workerId, CancellationToken ct)
    {
        if (!options.Value.Enabled)
        {
            return false;
        }
        await store.RefreshCollectingRunsAsync(ct);
        var lease = await store.TryLeaseRunAsync(workerId, clock.GetCurrentInstant(), ct);
        if (lease == null)
        {
            return false;
        }
        using var owned = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var renewal = Renew(lease, owned);
        try
        {
            if (!await store.SetRunStageAsync(lease, "collectingEvidence", owned.Token))
            {
                return true;
            }
            if (!await store.SetRunStageAsync(lease, "snapshotting", owned.Token))
            {
                return true;
            }
            await new AuditSnapshotService(db, store, lease, options.Value.MaxSnapshotBytes).CreateAsync(
                lease.RunId,
                owned.Token
            );
            var snapshot =
                await store.ReadSnapshotAsync(lease.RunId, owned.Token)
                ?? throw new AuditStoreException("snapshot_unavailable", 410);
            if (!await store.SetRunStageAsync(lease, "verifying", owned.Token))
            {
                return true;
            }
            var input = AuditCanonicalJson.Deserialize<AuditInput>(snapshot.Bytes);
            var result = await _verifier.VerifyAsync(input, owned.Token);
            owned.Token.ThrowIfCancellationRequested();
            await store.TryPublishAsync(lease, result, owned.Token);
        }
        catch (OperationCanceledException) when (owned.IsCancellationRequested)
        { /* Shutdown or lost ownership leaves durable recovery to the next lease. */
        }
        catch (AuditStoreException ex)
        {
            await store.FailRunAsync(lease, ex.Code, ct);
        }
        catch (AuditValidationException ex) when (ex.Code == "incompatible_audit_verifier")
        {
            await store.FailRunAsync(lease, ex.Code, ct, terminal: true);
        }
        catch (Exception)
        {
            await store.FailRunAsync(lease, "audit_execution_failed", ct);
        }
        finally
        {
            await owned.CancelAsync();
            await renewal;
        }
        return true;
    }

    private async Task Renew(RunLease lease, CancellationTokenSource owned)
    {
        try
        {
            using var timer = new PeriodicTimer(options.Value.RunLeaseRenewalInterval);
            while (await timer.WaitForNextTickAsync(owned.Token))
            {
                await using var renewalDb = new AiObservatoryDbContext(dbOptions);
                var renewalStore = new AuditStore(renewalDb, clock, options.Value);
                if (!await renewalStore.RenewRunLeaseAsync(lease, clock.GetCurrentInstant(), owned.Token))
                {
                    await owned.CancelAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (owned.IsCancellationRequested)
        { /* Shutdown or lost ownership leaves durable recovery to the next lease. */
        }
        catch (Exception)
        {
            await owned.CancelAsync();
        }
    }
}

public sealed class AuditHostedWorker(
    IServiceScopeFactory scopes,
    IOptions<AuditOptions> options,
    ILogger<AuditHostedWorker> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled || !options.Value.WorkerEnabled)
        {
            return;
        }
        var workerId = Guid.NewGuid();
        while (!stoppingToken.IsCancellationRequested)
        {
            var failed = false;
            try
            {
                using var scope = scopes.CreateScope();
                var worked = await scope
                    .ServiceProvider.GetRequiredService<AuditWorkerService>()
                    .ExecuteOneAsync(workerId, stoppingToken);
                if (worked)
                {
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                failed = true;
            }
            if (failed)
            {
                logger.LogWarning("audit_worker_retry");
            }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}

public interface IAuditRunVerifier
{
    Task<AuditResult> VerifyAsync(AuditInput input, CancellationToken ct);
}

public sealed class AuditRunVerifier : IAuditRunVerifier
{
    public Task<AuditResult> VerifyAsync(AuditInput input, CancellationToken ct)
    {
        if (
            input.SchemaVersion != AuditVerifierIdentity.SchemaVersion
            || input.VerifierVersion != AuditVerifierIdentity.VerifierVersion
            || input.RuleVersion != AuditVerifierIdentity.RuleVersion
        )
        {
            throw new AuditValidationException("incompatible_audit_verifier");
        }
        return Task.Run(() => new AuditVerifier().Verify(input), ct);
    }
}
