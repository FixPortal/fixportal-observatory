using AiObservatory.Data.Repositories;
using MimeKit;

namespace AiObservatory.Api.Services;

public sealed class EmailAlertNotifier(
    SmtpMailSender mailSender,
    IConfiguration config,
    IUsageRepository repository,
    ILogger<EmailAlertNotifier> logger
) : IAlertNotifier
{
    public async Task<AlertDeliveryResult> NotifyAsync(AlertMessage alert, CancellationToken ct = default)
    {
        var settings = await repository.GetNotificationSettingsAsync(ct);
        var to = settings?.AlertEmailTo;
        if (string.IsNullOrEmpty(to))
        {
            return AlertDeliveryResult.NoRecipientConfigured;
        }

        var user = mailSender.ReadSettings().User;
        // Blank-but-set falls through to the SMTP user: `??` only sees null, so an empty
        // BUDGET_ALERT_EMAIL_FROM would shadow a valid user and disable the channel outright
        // (the empty From fails the parse below). Same shape as ResolveMessageIdDomain.
        var from = config["BUDGET_ALERT_EMAIL_FROM"];
        if (string.IsNullOrWhiteSpace(from))
        {
            from = user;
        }

        // The recipient is runtime-editable and the startup backfill seed bypasses the
        // endpoint's validation, so an unparseable address can reach us. Treat it as
        // "not configured" rather than throwing: a throw releases the lease and retries
        // every pass forever, which no stored configuration value should be able to cause.
        if (!MailboxAddress.TryParse(to, out var toAddress))
        {
            logger.LogWarning(
                "Alert email recipient is not a valid mailbox address; treating the channel as unconfigured"
            );
            return AlertDeliveryResult.NoRecipientConfigured;
        }

        // An unset BUDGET_ALERT_SMTP_USER lands here as an empty string. Measured 2026-09-19:
        // that was production's state — no BUDGET_ALERT_SMTP_* setting existed on the app — so
        // every alert died here while two ingest sources had been failing for 18 and 20 days.
        // Reported as "not configured" rather than as a failure, because that is what it is.
        if (!MailboxAddress.TryParse(from, out var fromAddress))
        {
            logger.LogWarning(
                "Alert sender (BUDGET_ALERT_EMAIL_FROM / BUDGET_ALERT_SMTP_USER) is not a valid mailbox address; treating the channel as unconfigured"
            );
            return AlertDeliveryResult.NoRecipientConfigured;
        }

        using var message = new MimeMessage();
        message.From.Add(fromAddress);
        message.To.Add(toAddress);
        // Left unset when the caller supplies none, so MimeKit generates one. Assigning an
        // empty string instead would emit a malformed header.
        if (!string.IsNullOrWhiteSpace(alert.MessageId))
        {
            message.MessageId = alert.MessageId;
        }
        message.Subject = alert.Subject;
        message.Body = new TextPart("plain") { Text = alert.Body };

        await mailSender.SendAsync(message, ct);

        return AlertDeliveryResult.Sent;
    }
}
