using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace AiObservatory.Api.Services;

/// <summary>Resolved SMTP transport configuration. <see cref="User"/> doubles as the sender
/// fallback for callers that have no explicit From configured.</summary>
public sealed record SmtpSettings(string Host, int Port, string User, string Password);

/// <summary>
/// The SMTP transport step, shared by budget alerts and the daily source-health digest:
/// connect, authenticate when a user is configured, send, and always disconnect.
/// <para>
/// Deliberately knows nothing about WHAT is being sent. Recipient resolution, address
/// validation and delivery semantics stay with each caller, because those genuinely differ —
/// budget alerts are durable, leased and retried until a channel reports success, whereas the
/// digest is fire-and-forget and self-heals by repeating tomorrow. Sharing the transport and
/// not the semantics is the point of the split.
/// </para>
/// <para>
/// The env var names keep the <c>BUDGET_ALERT_</c> prefix they were introduced with: both
/// senders use the same mailbox, and renaming them would be a breaking infra change for a
/// cosmetic gain.
/// </para>
/// </summary>
public sealed class SmtpMailSender(ISmtpClient smtpClient, IConfiguration config)
{
    public SmtpSettings ReadSettings() =>
        new(
            config["BUDGET_ALERT_SMTP_HOST"] ?? "smtp.office365.com",
            int.TryParse(config["BUDGET_ALERT_SMTP_PORT"], out var port) ? port : 587,
            config["BUDGET_ALERT_SMTP_USER"] ?? string.Empty,
            config["BUDGET_ALERT_SMTP_PASS"] ?? string.Empty
        );

    public async Task SendAsync(MimeMessage message, CancellationToken ct)
    {
        var settings = ReadSettings();
        try
        {
            await smtpClient.ConnectAsync(settings.Host, settings.Port, SecureSocketOptions.StartTls, ct);
            if (!string.IsNullOrEmpty(settings.User))
            {
                await smtpClient.AuthenticateAsync(settings.User, settings.Password, ct);
            }

            await smtpClient.SendAsync(message, ct);
        }
        finally
        {
            if (smtpClient.IsConnected)
            {
                await smtpClient.DisconnectAsync(true, ct);
            }
        }
    }
}
