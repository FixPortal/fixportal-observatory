using System.Text.RegularExpressions;
using AiObservatory.Api.Services.GitHub;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace AiObservatory.Api.Services.Intelligence;

public class IntelligenceWorkerService(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<IntelligenceWorkerService> logger
) : BackgroundService
{
    private static readonly Duration GitHubBillingRefreshInterval = Duration.FromDays(1);

    // Test seam for the daily park between cycles: the unit lane observes the computed delay
    // and controls its completion instead of racing a real timer to prove the worker is parked.
    internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; init; } = Task.Delay;
    private static readonly Regex UriQuery = new(
        @"(https?://[^\s?]+)\?[^\s]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1)
    );

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await LogEnabledArmsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunAnalysisCatchupAsync(stoppingToken);
            await RunBudgetCheckAsync(stoppingToken);
            await RunGitHubBillingSyncAsync(stoppingToken);
            // Last: it reports on the state the arms above have just refreshed, so running it
            // here means the digest describes this cycle rather than the previous one.
            await RunSourceHealthDigestAsync(stoppingToken);

            var now = clock.GetCurrentInstant();
            var nextRun = now.InUtc().Date.PlusDays(1).AtMidnight().InUtc().ToInstant();
            var delay = (nextRun - now).ToTimeSpan();
            if (delay > TimeSpan.Zero)
            {
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
    }

    private async Task RunAnalysisCatchupAsync(CancellationToken stoppingToken)
    {
        try
        {
            var today = clock.GetCurrentInstant().InUtc().Date;
            var yesterday = today.PlusDays(-1);

            LocalDate? latestPeriodEnd;
            using (var scope = scopeFactory.CreateScope())
            {
                var repository = scope.ServiceProvider.GetRequiredService<IUsageRepository>();
                latestPeriodEnd = await repository.GetLatestInsightPeriodEndAsync(stoppingToken);
            }

            var start = latestPeriodEnd.HasValue ? latestPeriodEnd.Value.PlusDays(1) : yesterday;

            // Limit catch-up to a maximum of 7 days to prevent flooding on startup
            if (start < yesterday.PlusDays(-6))
            {
                start = yesterday.PlusDays(-6);
            }

            for (var date = start; date <= yesterday; date = date.PlusDays(1))
            {
                logger.LogInformation("Intelligence worker running analysis catchup for {Date}", date);
                await RunAnalysisAsync(date, stoppingToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Intelligence worker catchup failed");
        }
    }

    private async Task RunAnalysisAsync(LocalDate analysisDate, CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var generator = scope.ServiceProvider.GetRequiredService<IInsightGenerator>();
            var result = await generator.GenerateForDateAsync(analysisDate, ct);
            logger.LogInformation(
                "Intelligence worker wrote {Persisted} of {Generated} generated insights for {Period}",
                result.Persisted,
                result.Generated,
                analysisDate
            );
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Graceful shutdown - propagate so the host stops the worker cleanly.
            throw;
        }
        catch (Exception ex)
        {
            // Broad by design: isolate a single daily run's failure so the
            // long-running worker survives to the next iteration.
            logger.LogError(ex, "Intelligence worker failed for date {Date}", analysisDate);
        }
    }

    /// <summary>
    /// States once, at startup, which optional arms this worker actually has. Without it an
    /// unregistered arm is indistinguishable from a registered one that found nothing: both
    /// produce no output at all, so a silently dead arm reads as a quiet one. That cost real
    /// diagnosis time on the GitHub billing sync, which had been unregistered — and therefore
    /// returning immediately — while looking exactly like a sync finding no new spend.
    /// <para>
    /// Logged rather than enforced: an absent arm is a legitimate local and preview
    /// configuration, so this must never stop the worker starting.
    /// </para>
    /// </summary>
    internal async Task LogEnabledArmsAsync(CancellationToken ct = default)
    {
        bool gitHubBilling;
        bool digestHasRecipient;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            gitHubBilling = scope.ServiceProvider.GetService<GitHubBillingSyncService>() is not null;

            // The digest arm is always registered, but it can only ever SEND once a recipient
            // is configured. Reporting it as "enabled" on registration alone would reproduce,
            // in this very line, the failure the arm exists to prevent: something that reads
            // as live while it can never fire.
            var db = scope.ServiceProvider.GetService<AiObservatoryDbContext>();
            digestHasRecipient =
                db is not null
                && await db
                    .NotificationSettings.AsNoTracking()
                    .AnyAsync(
                        s => s.Id == NotificationSettings.SingletonId && s.AlertEmailTo != null && s.AlertEmailTo != "",
                        ct
                    );
        }
        catch (Exception ex)
        {
            // Diagnostics must not be the thing that stops the worker booting.
            logger.LogWarning(ex, "Intelligence worker could not determine which arms are enabled");
            return;
        }

        logger.LogInformation(
            "Intelligence worker arms — analysis catchup: enabled, budget check: enabled, "
                + "source health digest: {DigestState}, GitHub billing sync: {GitHubBillingState}",
            digestHasRecipient ? "enabled" : "NO RECIPIENT CONFIGURED (nothing will be sent)",
            gitHubBilling ? "enabled" : "NOT CONFIGURED (no entries will be written)"
        );
    }

    /// <summary>
    /// Mails a once-a-day summary of degraded ingest sources. Failures are logged and
    /// swallowed: a digest that cannot be delivered must never take down the arms that
    /// produce the data it reports on.
    /// </summary>
    private async Task RunSourceHealthDigestAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var digest = scope.ServiceProvider.GetRequiredService<SourceHealthDigestService>();
            await digest.SendIfDueAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Intelligence worker source health digest failed");
        }
    }

    /// <summary>
    /// Pulls GitHub's billed usage into the spend ledger. Daily rather than monthly: the
    /// sync upserts, so re-running keeps the open month's figure current instead of leaving
    /// the ledger a month behind. No-ops when the GitHub billing arm is not configured.
    /// </summary>
    private async Task RunGitHubBillingSyncAsync(CancellationToken ct)
    {
        var configured = false;
        Instant? attemptAt = null;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var states = scope.ServiceProvider.GetRequiredService<SourceSyncStateStore>();
            var now = clock.GetCurrentInstant();
            // Optional: registered only when GITHUB_TOKEN and GITHUB_BILLING_ORG are both set.
            if (scope.ServiceProvider.GetService<GitHubBillingSyncService>() is not { } sync)
            {
                await states.MarkUnconfiguredAsync(
                    UsageSourceIds.GitHubBillingApi,
                    GitHubBillingRefreshInterval,
                    now,
                    ct
                );
                return;
            }

            configured = true;
            attemptAt = now;
            await states.MarkAttemptAsync(UsageSourceIds.GitHubBillingApi, GitHubBillingRefreshInterval, now, ct);
            await sync.SyncAsync(ct);
            await states.MarkSuccessAsync(
                UsageSourceIds.GitHubBillingApi,
                GitHubBillingRefreshInterval,
                now,
                latestObservationAt: null,
                ct
            );
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (configured && attemptAt is { } failedAttemptAt)
            {
                try
                {
                    await using var statusScope = scopeFactory.CreateAsyncScope();
                    await statusScope
                        .ServiceProvider.GetRequiredService<SourceSyncStateStore>()
                        .MarkFailureAsync(
                            UsageSourceIds.GitHubBillingApi,
                            GitHubBillingRefreshInterval,
                            failedAttemptAt,
                            SanitizeError(ex.Message),
                            ct,
                            onlyIfLatestAttempt: true
                        );
                }
                catch (Exception statusException)
                {
                    logger.LogWarning(statusException, "Could not record GitHub billing source failure");
                }
            }
            // Broad by design, matching the steps above: a GitHub or FX outage must not
            // stop the worker reaching tomorrow's cycle.
            logger.LogError(ex, "Intelligence worker GitHub billing sync failed");
        }
    }

    internal static string SanitizeError(string error)
    {
        var sanitized = UriQuery.Replace(error.Replace('\r', ' ').Replace('\n', ' '), "$1");
        return sanitized.Length <= 500 ? sanitized : sanitized[..500];
    }

    private async Task RunBudgetCheckAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var svc = scope.ServiceProvider.GetRequiredService<BudgetAlertService>();
            await svc.CheckAndAlertAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Broad by design: keep the worker alive if the budget check fails
            // (DB error, HTTP delivery failure, etc.) so it retries next cycle.
            logger.LogError(ex, "Intelligence worker budget check failed");
        }
    }
}
