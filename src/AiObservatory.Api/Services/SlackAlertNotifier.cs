using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using AiObservatory.Data.Repositories;
using AiObservatory.Data.Security;
using NodaTime;

namespace AiObservatory.Api.Services;

/// <summary>
/// Posts a Slack incoming-webhook message. Best-effort: a failure is logged and swallowed by
/// the caller (<see cref="CompositeAlertNotifier"/>), never surfaced as a delivery failure
/// that would cause <c>BudgetAlertService</c> to re-attempt the whole payload (which would
/// re-send email too). Fenced by <see cref="BudgetAlertClaim.SlackSentAt"/> so a claim gets
/// one successful Slack delivery, not one per email retry cycle; a failed attempt is
/// re-attempted on a later pass. Rejections are classified: a 4xx (other than 429) is a
/// TERMINAL refusal — a rotated webhook URL, <c>channel_not_found</c>, <c>no_service</c> —
/// and is reported as <see cref="AlertDeliveryResult.PermanentlyRejected"/> so it is recorded
/// distinctly from a transient 5xx/timeout <see cref="AlertDeliveryResult.Failed"/>, the only
/// kind a retry can fix. The fence is check-then-act and deliberately not claimed before the
/// POST: a process death or DB failure between the POST and the fence write can re-post one
/// duplicate message, but Slack webhooks carry no idempotency key, so marking first would only
/// swap that rare duplicate for a rare silent loss -- the worse trade for an alerting channel.
/// </summary>
public sealed class SlackAlertNotifier(
    HttpClient http,
    IUsageRepository repository,
    IClock clock,
    ILogger<SlackAlertNotifier> logger
) : IAlertNotifier
{
    public async Task<AlertDeliveryResult> NotifyAsync(BudgetAlertPayload payload, CancellationToken ct = default)
    {
        var settings = await repository.GetNotificationSettingsAsync(ct);
        var webhookUrl = settings?.SlackWebhookUrl;
        if (string.IsNullOrEmpty(webhookUrl))
        {
            return AlertDeliveryResult.NoRecipientConfigured;
        }

        if (SlackWebhookProtector.IsUndecryptable(webhookUrl))
        {
            // The read path degrades an undecryptable stored webhook to a sentinel so the
            // settings row (and email alerting) keeps working. HERE is where the plaintext is
            // actually needed, so the hard failure lives here -- caught and logged by
            // CompositeAlertNotifier on every pass, never silently treated as "unconfigured".
            throw new InvalidOperationException(
                $"The stored Slack webhook URL could not be decrypted: {SlackWebhookProtector.KeyEnvironmentVariable} "
                    + "is unset or no longer matches the key the URL was encrypted with. Restore the key, or "
                    + "clear/replace the webhook via PUT /api/notification-settings -- the settings row still "
                    + "loads, so the API remedy is reachable."
            );
        }

        if (await repository.GetBudgetAlertSlackSentAsync(payload.ClaimId, ct))
        {
            // Fenced by a previous pass: the alert already reached Slack, so this channel
            // genuinely delivered even though this call itself posts nothing.
            return AlertDeliveryResult.Sent;
        }

        var text =
            string.Create(
                CultureInfo.InvariantCulture,
                $"*Budget alert: {payload.Provider} {payload.Period} billed spend exceeded £{payload.ThresholdGbp:F2}*\n"
            )
            + string.Create(
                CultureInfo.InvariantCulture,
                $"Total {payload.Period.ToLowerInvariant()} billed spend for {payload.Provider} reached £{payload.ActualSpendGbp:F2}, "
            )
            + string.Create(CultureInfo.InvariantCulture, $"exceeding your £{payload.ThresholdGbp:F2} threshold.");

        using var response = await http.PostAsJsonAsync(webhookUrl, new { text }, ct);
        if (!response.IsSuccessStatusCode)
        {
            // Slack returns the actionable reason (invalid_payload, channel_not_found,
            // no_service, ...) as a plain-text body; the status code alone cannot separate
            // a rotated webhook from a malformed payload. The body carries no secret -- the
            // webhook URL is the credential and must stay out of the log.
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            // A 4xx is static, not transient: the same request fails the same way on every
            // retry until an operator fixes the webhook, so it is recorded distinctly from a
            // retryable 5xx/timeout. 429 is the exception -- rate limiting clears on its own.
            var terminal =
                response.StatusCode
                is >= HttpStatusCode.BadRequest
                    and < HttpStatusCode.InternalServerError
                    and not HttpStatusCode.TooManyRequests;
            logger.LogError(
                "Slack webhook delivery failed with status {StatusCode} for budget alert {MessageId}: {ResponseBody}",
                response.StatusCode,
                payload.MessageId,
                responseBody
            );
            return terminal ? AlertDeliveryResult.PermanentlyRejected : AlertDeliveryResult.Failed;
        }

        await repository.MarkBudgetAlertSlackSentAsync(payload.ClaimId, clock.GetCurrentInstant(), ct);
        return AlertDeliveryResult.Sent;
    }
}
