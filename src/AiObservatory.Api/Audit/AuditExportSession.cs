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
    private readonly CancellationTokenRegistration _diagnosticCancellation;
    private FileStream? _spool;
    public AuditExportMaterialization Materialized { get; }
    public CancellationToken Token => _owned.Token;
    internal AuditExportDiagnostics? Diagnostics { get; }

    private AuditExportSession(
        AuditStore store,
        IServiceScopeFactory scopes,
        AuditExportLease lease,
        AuditExportMaterialization materialized,
        CancellationTokenSource owned,
        AuditExportDiagnostics? diagnostics
    )
    {
        _store = store;
        _scopes = scopes;
        _lease = lease;
        Materialized = materialized;
        _owned = owned;
        Diagnostics = diagnostics;
        _diagnosticCancellation = owned.Token.Register(() => diagnostics?.CancellationRequested());
        _monitor = Monitor();
    }

    public static Task<AuditExportSession> OpenAsync(
        AuditStore store,
        IServiceScopeFactory scopes,
        IClock clock,
        Guid runId,
        CancellationToken ct
    ) => OpenAsync(store, scopes, clock, runId, ct, null);

    internal static async Task<AuditExportSession> OpenAsync(
        AuditStore store,
        IServiceScopeFactory scopes,
        IClock clock,
        Guid runId,
        CancellationToken ct,
        AuditExportDiagnostics? diagnostics
    )
    {
        diagnostics?.Start("lease_acquire");
        AuditExportLease lease;
        try
        {
            lease = await store.AcquireExportLeaseAsync(runId, ct);
            diagnostics?.Complete();
        }
        catch (Exception error)
        {
            diagnostics?.Fail(error);
            throw;
        }
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
            diagnostics?.Start("materialize");
            var materialized = await store.MaterializeExportAsync(runId, owned.Token);
            diagnostics?.Materialized(materialized);
            diagnostics?.Complete();
            return new(store, scopes, lease, materialized, owned, diagnostics);
        }
        catch (Exception error)
        {
            diagnostics?.Fail(error);
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

    internal void OwnSpool(FileStream spool)
    {
        if (_spool != null)
        {
            throw new InvalidOperationException("An export session already owns a spool.");
        }
        _spool = spool;
    }

    public async ValueTask DisposeAsync()
    {
        await _diagnosticCancellation.DisposeAsync();
        await _owned.CancelAsync();
        await _monitor;
        try
        {
            // The inner HTTP result may never execute. Close private evidence before releasing
            // its durable reservation so deletion cannot finish while the prepared ZIP remains open.
            if (_spool != null)
            {
                await _spool.DisposeAsync();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AuditStoreException("bundle_spool_unavailable", 503);
        }
        finally
        {
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
                session.Diagnostics?.Start("http_body");
                session.Token.ThrowIfCancellationRequested();
                await inner.ExecuteAsync(httpContext);
                session.Diagnostics?.Complete();
            }
            catch (IOException error)
            {
                session.Diagnostics?.Fail(error);
                httpContext.Abort();
            }
            catch (OperationCanceledException error) when (session.Token.IsCancellationRequested)
            {
                session.Diagnostics?.Fail(error);
                httpContext.Abort();
            }
            catch (Exception error)
            {
                session.Diagnostics?.Fail(error);
                throw;
            }
        }
    }
}
