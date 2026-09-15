using AiObservatory.Data;
using AiObservatory.Data.Entities;
using Microsoft.EntityFrameworkCore;
using MimeKit;
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
/// </summary>
public sealed class SourceHealthDigestService(
    AiObservatoryDbContext db,
    SmtpMailSender mailSender,
    IConfiguration config,
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

        var settings = await db
            .NotificationSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == NotificationSettings.SingletonId, ct);
        if (string.IsNullOrWhiteSpace(settings?.AlertEmailTo))
        {
            logger.LogInformation(
                "Source health digest: {Count} source(s) degraded but no alert recipient is configured",
                digest.DegradedCount
            );
            return false;
        }

        if (!MailboxAddress.TryParse(settings.AlertEmailTo, out var toAddress))
        {
            // Same reasoning as EmailAlertNotifier: a stored value must never be able to
            // throw us into a retry loop, so an unparseable address is "not configured".
            logger.LogWarning("Source health digest: alert recipient is not a valid mailbox address");
            return false;
        }

        var from = config["BUDGET_ALERT_EMAIL_FROM"];
        if (string.IsNullOrWhiteSpace(from))
        {
            from = mailSender.ReadSettings().User;
        }

        if (!MailboxAddress.TryParse(from, out var fromAddress))
        {
            logger.LogWarning("Source health digest: sender is not a valid mailbox address");
            return false;
        }

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
            logger.LogDebug("Source health digest: already sent for {Date}", today);
            return false;
        }

        using var message = new MimeMessage();
        message.From.Add(fromAddress);
        message.To.Add(toAddress);
        message.Subject = digest.Subject;
        message.Body = new TextPart("plain") { Text = digest.Body };

        try
        {
            await mailSender.SendAsync(message, ct);
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

        logger.LogInformation(
            "Source health digest: sent for {Date} covering {Count} degraded source(s)",
            today,
            digest.DegradedCount
        );
        return true;
    }
}
