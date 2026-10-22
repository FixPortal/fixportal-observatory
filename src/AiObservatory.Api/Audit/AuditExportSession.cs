using AiObservatory.Data.Audit;
using NodaTime;

namespace AiObservatory.Api.Audit;

/// <summary>A hard, nonrenewable read lease. Its lifetime includes the actual HTTP body.</summary>
public sealed class AuditExportSession : IAsyncDisposable
{
    private readonly AuditStore _store;
    private readonly IServiceScopeFactory _scopes;
    private readonly AuditExportLease _lease;
    private readonly CancellationTokenSource _owned;
    private readonly Task _monitor;
    public AuditExportMaterialization Materialized { get; }
    public CancellationToken Token => _owned.Token;

    private AuditExportSession(
        AuditStore store,
        IServiceScopeFactory scopes,
        AuditExportLease lease,
        AuditExportMaterialization materialized,
        CancellationTokenSource owned
    )
    {
        _store = store;
        _scopes = scopes;
        _lease = lease;
        Materialized = materialized;
        _owned = owned;
        _monitor = Monitor();
    }

    public static async Task<AuditExportSession> OpenAsync(
        AuditStore store,
        IServiceScopeFactory scopes,
        IClock clock,
        Guid runId,
        CancellationToken ct
    )
    {
        var lease = await store.AcquireExportLeaseAsync(runId, ct);
        var owned = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var remaining = lease.ExpiresAt - clock.GetCurrentInstant();
        if (remaining <= Duration.Zero)
        {
            owned.Dispose();
            await store.ReleaseExportLeaseAsync(lease.Id, CancellationToken.None);
            throw new AuditStoreException("evidence_expired", 410);
        }
        owned.CancelAfter(remaining.ToTimeSpan());
        try
        {
            var materialized = await store.MaterializeExportAsync(runId, owned.Token);
            return new(store, scopes, lease, materialized, owned);
        }
        catch
        {
            owned.Dispose();
            await store.ReleaseExportLeaseAsync(lease.Id, CancellationToken.None);
            throw;
        }
    }

    private async Task Monitor()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            while (await timer.WaitForNextTickAsync(Token))
            {
                using var scope = _scopes.CreateScope();
                if (
                    !await scope
                        .ServiceProvider.GetRequiredService<AuditStore>()
                        .ExportLeaseValidAsync(_lease.Id, Token)
                )
                {
                    await _owned.CancelAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (_owned.IsCancellationRequested)
        { /* Lease release or response cancellation ends monitoring. */
        }
        catch (Exception)
        {
            await _owned.CancelAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _owned.CancelAsync();
        await _monitor;
        using var releaseCeiling = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await _store.ReleaseExportLeaseAsync(_lease.Id, releaseCeiling.Token);
        }
        finally
        {
            _owned.Dispose();
        }
    }
}

public sealed class AuditLeasedHttpResult(AuditExportSession session, IResult inner) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        await using (session)
        {
            using var abort = session.Token.Register(httpContext.Abort);
            try
            {
                session.Token.ThrowIfCancellationRequested();
                await inner.ExecuteAsync(httpContext);
            }
            catch (IOException)
            {
                httpContext.Abort();
            }
            catch (OperationCanceledException) when (session.Token.IsCancellationRequested)
            {
                httpContext.Abort();
            }
        }
    }
}
