using System.Globalization;
using AiObservatory.Data.Repositories;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace AiObservatory.Api.Services;

public sealed class EmailAlertNotifier(
    ISmtpClient smtpClient,
    IConfiguration config,
    IUsageRepository repository,
    ILogger<EmailAlertNotifier> logger
) : IAlertNotifier
{
    public async Task<AlertDeliveryResult> NotifyAsync(BudgetAlertPayload payload, CancellationToken ct = default)
    {
        var settings = await repository.GetNotificationSettingsAsync(ct);
        var to = settings?.AlertEmailTo;
        if (string.IsNullOrEmpty(to))
        {
            return AlertDeliveryResult.NoRecipientConfigured;
        }

        var host = config["BUDGET_ALERT_SMTP_HOST"] ?? "smtp.office365.com";
        var port = int.TryParse(config["BUDGET_ALERT_SMTP_PORT"], out var p) ? p : 587;
        var user = config["BUDGET_ALERT_SMTP_USER"] ?? string.Empty;
        var pass = config["BUDGET_ALERT_SMTP_PASS"] ?? string.Empty;
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
                "Budget alert email recipient is not a valid mailbox address; treating the channel as unconfigured"
            );
            return AlertDeliveryResult.NoRecipientConfigured;
        }

        if (!MailboxAddress.TryParse(from, out var fromAddress))
        {
            logger.LogWarning(
                "Budget alert sender (BUDGET_ALERT_EMAIL_FROM / BUDGET_ALERT_SMTP_USER) is not a valid mailbox address; treating the channel as unconfigured"
            );
            return AlertDeliveryResult.NoRecipientConfigured;
        }

        try
        {
            await smtpClient.ConnectAsync(host, port, SecureSocketOptions.StartTls, ct);
            if (!string.IsNullOrEmpty(user))
            {
                await smtpClient.AuthenticateAsync(user, pass, ct);
            }

            using var message = new MimeMessage();
            message.From.Add(fromAddress);
            message.To.Add(toAddress);
            message.MessageId = payload.MessageId;
            message.Subject = string.Create(
                CultureInfo.InvariantCulture,
                $"Budget alert: {payload.Provider} {payload.Period} billed spend exceeded £{payload.ThresholdGbp:F2}"
            );
            message.Body = new TextPart("plain")
            {
                Text =
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Total {payload.Period.ToLowerInvariant()} billed spend for {payload.Provider} reached £{payload.ActualSpendGbp:F2}, "
                    )
                    + string.Create(
                        CultureInfo.InvariantCulture,
                        $"exceeding your £{payload.ThresholdGbp:F2} threshold.\n\nTriggered at: {payload.TriggeredAt:u}"
                    ),
            };

            await smtpClient.SendAsync(message, ct);
        }
        finally
        {
            if (smtpClient.IsConnected)
            {
                await smtpClient.DisconnectAsync(true, ct);
            }
        }

        return AlertDeliveryResult.Sent;
    }
}
