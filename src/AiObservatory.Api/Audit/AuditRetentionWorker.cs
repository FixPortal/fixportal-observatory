using Microsoft.Extensions.Options;
using NodaTime;

namespace AiObservatory.Api.Audit;

public sealed class AuditRetentionWorker(
    IServiceScopeFactory scopes,
    IOptions<AuditOptions> options,
    IClock clock,
    ILogger<AuditRetentionWorker> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled || !options.Value.WorkerEnabled)
        {
            return;
        }
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (!stoppingToken.IsCancellationRequested)
        {
            var failed = false;
            try
            {
                using var scope = scopes.CreateScope();
                await scope
                    .ServiceProvider.GetRequiredService<AuditRetentionService>()
                    .ExpireAsync(clock.GetCurrentInstant(), stoppingToken);
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
                logger.LogWarning("Audit retention cleanup failed; retrying next hour");
            }
            if (!await timer.WaitForNextTickAsync(stoppingToken))
            {
                return;
            }
        }
    }
}
