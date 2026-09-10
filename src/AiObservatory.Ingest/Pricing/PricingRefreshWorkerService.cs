using AiObservatory.Data.Pricing;
using AiObservatory.Data.Repositories;
using AiObservatory.Ingest.Sources;
using NodaTime;

namespace AiObservatory.Ingest.Pricing;

public sealed class PricingRefreshWorkerService(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<PricingRefreshWorkerService> logger
) : BackgroundService
{
    private static readonly Duration RefreshInterval = Duration.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // One transient fault (a catalog read, a DI resolution, a state read between the
            // guarded paths) must not take the host down — BackgroundServiceExceptionBehavior
            // defaults to StopHost. A failed pass retries on the next daily tick.
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogRefreshPassFailure(exception);
            }
            await Task.Delay(TimeSpan.FromDays(1), stoppingToken);
        }
    }

    // The exception object is NOT passed to the log: providers render it as a full ToString()
    // beside the sanitized field, defeating the query-string redaction.
    private void LogRefreshPassFailure(Exception exception) =>
        logger.LogError(
            "Daily pricing refresh pass failed: {Error}",
            ProviderPollingWorkerService.SanitizeError(exception.Message)
        );

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        await services.GetRequiredService<BundledPricingCatalogLoader>().LoadAsync(cancellationToken);

        var sources = services.GetServices<IPricingSource>().ToDictionary(source => source.SourceId);
        var definitions = services.GetServices<PricingSourceDefinition>().ToList();
        var states = services.GetRequiredService<SourceSyncStateStore>();
        var store = services.GetRequiredService<PricingSnapshotStore>();
        var repricing = services.GetRequiredService<PricingRepricingService>();
        var now = clock.GetCurrentInstant();

        foreach (var definition in definitions)
        {
            if (!definition.IsConfigured || !sources.TryGetValue(definition.SourceId, out var source))
            {
                await MarkUnconfiguredAsync(definition, states, now, cancellationToken);
                continue;
            }

            var state = await states.GetAsync(source.SourceId, cancellationToken);
            if (state?.LastSuccessAt > now - RefreshInterval)
            {
                continue;
            }

            await RefreshAsync(source, definition, states, store, repricing, now, cancellationToken);
        }
    }

    private async Task MarkUnconfiguredAsync(
        PricingSourceDefinition definition,
        SourceSyncStateStore states,
        Instant now,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await states.MarkUnconfiguredAsync(
                definition.SourceId,
                definition.ExpectedRefreshInterval,
                now,
                cancellationToken
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogStateWriteFailure(definition.SourceId, exception, "pricing state");
        }
    }

    private async Task RefreshAsync(
        IPricingSource source,
        PricingSourceDefinition definition,
        SourceSyncStateStore states,
        PricingSnapshotStore store,
        PricingRepricingService repricing,
        Instant now,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await states.MarkAttemptAsync(source.SourceId, definition.ExpectedRefreshInterval, now, cancellationToken);
            var candidate = await source.FetchAsync(cancellationToken);
            if (candidate is not null)
            {
                await store.ActivateAsync(
                    candidate,
                    cancellationToken,
                    (_, callbackCt) => repricing.RepriceProviderAsync(candidate.Provider, callbackCt)
                );
            }
            await states.MarkSuccessAsync(
                source.SourceId,
                definition.ExpectedRefreshInterval,
                now,
                candidate?.RetrievedAt,
                cancellationToken
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await PersistFailureAsync(source.SourceId, definition, states, now, exception, cancellationToken);
        }
    }

    private async Task PersistFailureAsync(
        string sourceId,
        PricingSourceDefinition definition,
        SourceSyncStateStore states,
        Instant now,
        Exception exception,
        CancellationToken cancellationToken
    )
    {
        var error = ProviderPollingWorkerService.SanitizeError(exception.Message);
        // Log before persisting: if the state write itself throws, the fallback catch must
        // not be the only record — it would lose the upstream cause during a compound outage.
        logger.LogError("{SourceId} pricing refresh failed: {Error}", sourceId, error);
        try
        {
            await states.MarkFailureAsync(
                sourceId,
                definition.ExpectedRefreshInterval,
                now,
                error,
                cancellationToken,
                onlyIfLatestAttempt: true
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception stateException)
        {
            LogStateWriteFailure(sourceId, stateException, "pricing failure state");
        }
    }

    private void LogStateWriteFailure(string sourceId, Exception exception, string operation) =>
        logger.LogError(
            "{SourceId} {Operation} could not be persisted: {Error}",
            sourceId,
            operation,
            ProviderPollingWorkerService.SanitizeError(exception.Message)
        );
}
