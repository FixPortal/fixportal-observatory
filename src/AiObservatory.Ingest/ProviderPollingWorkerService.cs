using System.Text.RegularExpressions;
using AiObservatory.Data.Repositories;
using AiObservatory.Ingest.Sources;
using Microsoft.Extensions.Options;
using NodaTime;

namespace AiObservatory.Ingest;

public class ProviderPollingWorkerService(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<ProviderPollingWorkerService> logger,
    IOptions<IngestOptions> options
) : BackgroundService
{
    private const int ConsecutiveFailureAlertThreshold = 3;
    private static readonly Regex UriQuery = new(
        @"(https?://[^\s?]+)\?[^\s]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1)
    );

    private long _lastCycleCompletedTicks;
    private int _cyclesCompleted;

    public Instant? LastCycleCompletedAt =>
        CyclesCompleted == 0 ? null : Instant.FromUnixTimeTicks(Interlocked.Read(ref _lastCycleCompletedTicks));

    public int CyclesCompleted => Volatile.Read(ref _cyclesCompleted);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogEnabledArms();

        var interval = options.Value.PollingInterval;
        var lookbackDays = Math.Max(1, options.Value.LookbackDays);
        logger.LogInformation(
            "Provider polling worker started (interval: {Interval}, lookback: {LookbackDays}d)",
            interval,
            lookbackDays
        );
        while (!stoppingToken.IsCancellationRequested)
        {
            var through = clock.GetCurrentInstant().InUtc().Date.PlusDays(-1);
            var from = through.PlusDays(-(lookbackDays - 1));
            await RunPollAsync(from, through, stoppingToken);
            Interlocked.Exchange(ref _lastCycleCompletedTicks, clock.GetCurrentInstant().ToUnixTimeTicks());
            Interlocked.Increment(ref _cyclesCompleted);
            await Task.Delay(interval, stoppingToken);
        }
    }

    private void LogEnabledArms()
    {
        using var scope = scopeFactory.CreateScope();
        var sources = scope.ServiceProvider.GetServices<IUsageSource>().Select(x => x.SourceId).ToHashSet();
        var definitions = scope.ServiceProvider.GetServices<SourceDefinition>();
        var states = definitions.Select(definition =>
            $"{definition.SourceId}: {(definition.IsConfigured && sources.Contains(definition.SourceId) ? "enabled" : "NOT CONFIGURED")}"
        );
        logger.LogInformation("Provider polling arms — {SourceStates}", string.Join(", ", states));
    }

    public async Task RunPollAsync(LocalDate from, LocalDate through, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var sources = services.GetServices<IUsageSource>().ToDictionary(source => source.SourceId);
        var definitions = services.GetServices<SourceDefinition>().ToList();
        var stateStore = services.GetRequiredService<SourceSyncStateStore>();
        var current = clock.GetCurrentInstant();

        foreach (
            var definition in definitions.Where(definition =>
                !definition.IsConfigured || !sources.ContainsKey(definition.SourceId)
            )
        )
        {
            try
            {
                await stateStore.MarkUnconfiguredAsync(
                    definition.SourceId,
                    definition.ExpectedRefreshInterval,
                    current,
                    cancellationToken
                );
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogStateWriteFailure(definition.SourceId, ex);
            }
        }

        foreach (
            var definition in definitions.Where(definition =>
                definition.IsConfigured && sources.ContainsKey(definition.SourceId)
            )
        )
        {
            await PollSourceAsync(
                sources[definition.SourceId],
                definition,
                stateStore,
                current,
                from,
                through,
                cancellationToken
            );
        }
    }

    private async Task PollSourceAsync(
        IUsageSource source,
        SourceDefinition definition,
        SourceSyncStateStore stateStore,
        Instant current,
        LocalDate from,
        LocalDate through,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var state = await stateStore.GetAsync(source.SourceId, cancellationToken);
            var sourceFrom = state?.LastSuccessAt is { } lastSuccessAt
                ? LocalDate.Min(from, lastSuccessAt.InUtc().Date)
                : from;
            sourceFrom = state?.PendingFromDate is { } pendingFromDate
                ? LocalDate.Min(sourceFrom, pendingFromDate)
                : sourceFrom;
            await stateStore.MarkAttemptAsync(
                source.SourceId,
                definition.ExpectedRefreshInterval,
                current,
                cancellationToken,
                sourceFrom
            );
            var result = await source.IngestAsync(sourceFrom, through, cancellationToken);
            if (result.FailedRepoCount > 0)
            {
                // A partially failed cycle is not a success: MarkSuccessAsync would stamp
                // LastSuccessAt, clear PendingFromDate and report the source healthy, so the
                // failed lanes' window between the old watermark and the lookback edge could
                // never be re-fetched. Persist a degraded state instead, which keeps the
                // recovery state and escalates through the usual failure counter.
                await PersistFailureAsync(
                    source.SourceId,
                    definition,
                    stateStore,
                    current,
                    $"{result.FailedRepoCount} repo(s) failed to ingest this cycle",
                    isUnavailable: false,
                    cancellationToken
                );
                return;
            }

            await stateStore.MarkSuccessAsync(
                source.SourceId,
                definition.ExpectedRefreshInterval,
                current,
                result.LatestObservationAt,
                cancellationToken
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SourceUnavailableException ex)
        {
            var error = SanitizeError(ex.Message);
            await PersistFailureAsync(
                source.SourceId,
                definition,
                stateStore,
                current,
                error,
                isUnavailable: true,
                cancellationToken
            );
        }
        catch (Exception ex)
        {
            var error = SanitizeError(ex.Message);
            await PersistFailureAsync(
                source.SourceId,
                definition,
                stateStore,
                current,
                error,
                isUnavailable: false,
                cancellationToken
            );
        }
    }

    private async Task PersistFailureAsync(
        string sourceId,
        SourceDefinition definition,
        SourceSyncStateStore stateStore,
        Instant current,
        string error,
        bool isUnavailable,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var count = isUnavailable
                ? await stateStore.MarkUnavailableAsync(
                    sourceId,
                    definition.ExpectedRefreshInterval,
                    current,
                    error,
                    cancellationToken,
                    onlyIfLatestAttempt: true
                )
                : await stateStore.MarkFailureAsync(
                    sourceId,
                    definition.ExpectedRefreshInterval,
                    current,
                    error,
                    cancellationToken,
                    onlyIfLatestAttempt: true
                );
            if (count >= 0)
            {
                LogFailure(sourceId, count, error);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Include the source error: persisting the failure state failing must not hide
            // the upstream cause — the compound outage is exactly when it is needed most.
            LogCompoundFailure(sourceId, error, ex);
        }
    }

    // The exception itself is NOT passed to the log: providers render it as a full ToString()
    // beside the sanitized field, defeating the query-string redaction (signed download URLs
    // carry SAS tokens there). Same shape as LogStateWriteFailure.
    private void LogCompoundFailure(string sourceId, string error, Exception exception) =>
        logger.LogError(
            "{SourceId} ingestion failed: {Error} — and the failure state could not be persisted: {StateError}",
            sourceId,
            error,
            SanitizeError(exception.Message)
        );

    private void LogStateWriteFailure(string sourceId, Exception exception) =>
        logger.LogError(
            "{SourceId} ingestion state could not be persisted: {Error}",
            sourceId,
            SanitizeError(exception.Message)
        );

    private void LogFailure(string sourceId, int count, string error)
    {
        if (count >= ConsecutiveFailureAlertThreshold)
        {
            logger.LogError(
                "{SourceId} ingestion has failed {Count} consecutive polls — source may be misconfigured or unavailable: {Error}",
                sourceId,
                count,
                error
            );
            return;
        }

        logger.LogError("{SourceId} ingestion failed: {Error}", sourceId, error);
    }

    internal static string SanitizeError(string error)
    {
        var sanitized = UriQuery.Replace(error.Replace('\r', ' ').Replace('\n', ' '), "$1");
        return sanitized.Length <= 500 ? sanitized : sanitized[..500];
    }
}
