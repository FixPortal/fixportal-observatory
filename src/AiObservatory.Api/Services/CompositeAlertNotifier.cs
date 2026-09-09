namespace AiObservatory.Api.Services;

/// <summary>
/// Fans a budget alert out to both delivery channels. Email keeps its existing at-least-once
/// retry semantics from before this class existed: its failure propagates unchanged, so
/// <c>BudgetAlertService</c>'s lease is released and the whole delivery retries. Slack is a
/// best-effort secondary channel fenced by <c>BudgetAlertClaim.SlackSentAt</c> (see
/// <see cref="SlackAlertNotifier"/>): a failure is logged and never retried in isolation, and
/// never blocks email from being attempted or from correctly reporting its own outcome upward.
/// Because Slack runs first inside this same <see cref="NotifyAsync"/> call, it is attempted on
/// every email lease-retry pass, but the fence makes every successful delivery after the first a
/// no-op -- Slack delivers at most once per claim, independent of how many times email itself is
/// retried. The fence is check-then-act (see <see cref="SlackAlertNotifier"/>), so a crash
/// between the Slack POST and the fence write can post one duplicate; that is accepted.
/// </summary>
public sealed class CompositeAlertNotifier(
    [FromKeyedServices("email")] IAlertNotifier email,
    [FromKeyedServices("slack")] IAlertNotifier slack,
    ILogger<CompositeAlertNotifier> logger
) : IAlertNotifier
{
    public async Task<AlertDeliveryResult> NotifyAsync(BudgetAlertPayload payload, CancellationToken ct = default)
    {
        var slackResult = AlertDeliveryResult.Failed;
        try
        {
            slackResult = await slack.NotifyAsync(payload, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Slack alert delivery failed for budget alert {MessageId}", payload.MessageId);
        }

        var emailResult = await email.NotifyAsync(payload, ct);
        if (slackResult == AlertDeliveryResult.Sent || emailResult == AlertDeliveryResult.Sent)
        {
            return AlertDeliveryResult.Sent;
        }

        // Precedence after Sent: a transient failure outranks a terminal rejection (a later
        // pass may still deliver through the transiently-failing channel), which outranks a
        // bare "nothing configured" -- the most specific reason the claim did not deliver wins,
        // so the delivery log can tell a dead webhook apart from an unconfigured instance.
        if (slackResult == AlertDeliveryResult.Failed || emailResult == AlertDeliveryResult.Failed)
        {
            return AlertDeliveryResult.Failed;
        }

        return
            slackResult == AlertDeliveryResult.PermanentlyRejected
            || emailResult == AlertDeliveryResult.PermanentlyRejected
            ? AlertDeliveryResult.PermanentlyRejected
            : AlertDeliveryResult.NoRecipientConfigured;
    }
}
