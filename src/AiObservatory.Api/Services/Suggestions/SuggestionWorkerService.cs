using Microsoft.Extensions.Options;
using NodaTime;

namespace AiObservatory.Api.Services.Suggestions;

/// <summary>
/// Evaluates the suggestion rules once at startup and then daily at the next UTC midnight. A separate
/// worker from the insights job on purpose: suggestions use no LLM, so they must not depend on that
/// job being enabled or healthy.
/// </summary>
public class SuggestionWorkerService(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    IOptions<SuggestionOptions> options,
    ILogger<SuggestionWorkerService> logger
) : BackgroundService
{
    // Test seam for the daily park between cycles, as in IntelligenceWorkerService.
    internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; init; } = Task.Delay;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.WorkerEnabled)
        {
            logger.LogInformation("Suggestions worker disabled by Suggestions:WorkerEnabled");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);

            var now = clock.GetCurrentInstant();
            var nextRun = now.InUtc().Date.PlusDays(1).AtMidnight().InUtc().ToInstant();
            var delay = (nextRun - now).ToTimeSpan();
            if (delay <= TimeSpan.Zero)
            {
                continue;
            }
            try
            {
                await DelayAsync(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<SuggestionService>();
            var outcomes = await service.EvaluateAsync(ct);
            logger.LogInformation(
                "Suggestions evaluated: {Rules} rules, {Evaluated} evaluated, {Suggestions} firing",
                outcomes.Count,
                outcomes.Count(o => o.Evaluated),
                outcomes.Sum(o => o.Candidates.Count)
            );
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Broad by design: a failed run must not stop the worker; the next cycle retries.
            logger.LogError(ex, "Suggestion evaluation failed");
        }
    }
}
