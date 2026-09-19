using AiObservatory.Data;
using AiObservatory.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace AiObservatory.Api.Services;

/// <summary>
/// Sends one source-health digest per UTC day, and only when something is actually degraded.
/// <para>
/// Exists because a degraded source is already recorded faithfully — <c>/api/sources/status</c>
/// reported <c>github-activity-api</c> as unavailable with 306 consecutive failures — and still
/// went unnoticed for twelve days, because nothing reads that endpoint unprompted. The state was
/// never the gap; the notification was.
/// </para>
/// <para>
/// Delivers through <see cref="IAlertNotifier"/> rather than straight to SMTP, so it reaches
/// every configured channel instead of only the one it was written against. That matters more
/// than it reads: measured 2026-09-19, production had no SMTP settings at all, so the digest's
/// direct mail path could never fire — and the failure it exists to announce went unannounced
/// for eighteen days, the second time this feature was defeated by delivery rather than by
/// detection.
/// </para>
/// </summary>
public sealed class SourceHealthDigestService(
    AiObservatoryDbContext db,
    IAlertNotifier notifier,
    IClock clock,
    ILogger<SourceHealthDigestService> logger
)
{
    /// <summary>Returns true only when a digest was actually handed to SMTP.</summary>
    public async Task<bool> SendIfDueAsync(CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        var today = now.InUtc().Date;

        var states = await db.SourceSyncStates.AsNoTracking().ToListAsync(ct);
        var digest = SourceHealthDigest.Compose(states, now);
        if (digest is null)
        {
            logger.LogInformation("Source health digest: no degraded sources; nothing to send");
            return false;
        }

        // Recipient and sender validation now lives in the notifiers, which is why this method
        // no longer refuses before claiming: whether any channel can deliver is only known once
        // one has been asked. The NoRecipientConfigured arm below restores the claim for that
        // case, so "nothing configured" still costs nothing.
        var previousDigestOn = await db
            .NotificationSettings.AsNoTracking()
            .Where(s => s.Id == NotificationSettings.SingletonId)
            .Select(s => s.LastSourceHealthDigestOn)
            .FirstOrDefaultAsync(ct);

        // Claim the day BEFORE sending, and never release it on failure. A digest is
        // fire-and-forget by design: losing one to an SMTP outage costs a day's notice and
        // self-heals tomorrow, whereas releasing the claim would re-send on every worker pass
        // and restart for the rest of the day. The opposite trade to BudgetAlertClaim, which
        // holds a lease precisely because a missed threshold alert never comes round again.
        //
        // The null arm of the predicate is load-bearing: in SQL `NULL <> DATE '...'` evaluates
        // to NULL, not true, so a settings row that has never sent a digest would never be
        // claimed and the feature would silently never fire.
        var claimed = await db
            .NotificationSettings.Where(s =>
                s.Id == NotificationSettings.SingletonId
                && (s.LastSourceHealthDigestOn == null || s.LastSourceHealthDigestOn != today)
            )
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.LastSourceHealthDigestOn, today), ct);

        if (claimed == 0)
        {
            // Either today's digest has already gone out, or no settings row exists at all —
            // the row is created by the notification-settings endpoint on first write.
            logger.LogDebug("Source health digest: already sent for {Date}, or no settings row exists", today);
            return false;
        }

        // No Message-Id: the digest is never retried under its own identity, so a stable id
        // would only let a receiving server collapse tomorrow's digest into today's.
        var alert = new AlertMessage(digest.Subject, digest.Body);

        AlertDeliveryResult result;
        try
        {
            result = await notifier.NotifyAsync(alert, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Source health digest: delivery failed for {Date}", today);
            return false;
        }

        if (result == AlertDeliveryResult.Sent)
        {
            logger.LogInformation(
                "Source health digest: sent for {Date} covering {Count} degraded source(s)",
                today,
                digest.DegradedCount
            );
            return true;
        }

        if (result == AlertDeliveryResult.NoRecipientConfigured)
        {
            // Nothing left this process, so restoring the claim cannot duplicate a delivery —
            // and it means an operator who configures a channel later today still gets today's
            // digest rather than waiting for tomorrow's. Deliberately NOT done for Failed or
            // PermanentlyRejected: there the send may have partially happened, and re-claiming
            // would re-attempt on every worker pass for the rest of the day.
            await db
                .NotificationSettings.Where(s =>
                    s.Id == NotificationSettings.SingletonId && s.LastSourceHealthDigestOn == today
                )
                .ExecuteUpdateAsync(set => set.SetProperty(s => s.LastSourceHealthDigestOn, previousDigestOn), ct);

            logger.LogInformation(
                "Source health digest: {Count} source(s) degraded but no channel is configured; today's claim was restored",
                digest.DegradedCount
            );
            return false;
        }

        logger.LogError(
            "Source health digest: no channel delivered for {Date} ({Outcome}); today's digest is lost and will not be retried",
            today,
            result
        );
        return false;
    }
}
