namespace AiObservatory.Api.Services;

/// <summary>
/// Durable budget-alert deliveries have at-least-once attempt semantics. Retries preserve
/// <see cref="BudgetAlertPayload.MessageId"/>, but SMTP can accept a message before the sender
/// observes failure, so recipients may still see a duplicate. No exactly-once claim is made.
/// </summary>
public record BudgetAlertPayload(
    string Provider,
    string Period,
    decimal ThresholdGbp,
    decimal ActualSpendGbp,
    DateTimeOffset TriggeredAt,
    string MessageId,
    Guid ClaimId
);

/// <summary>
/// Outcome of one <see cref="IAlertNotifier.NotifyAsync"/> call. "Returned without throwing"
/// is not delivery: a notifier that no-ops must say so explicitly, because
/// <c>BudgetAlertService</c> only closes the durable claim when a channel reports
/// <see cref="Sent"/>. Listed so the default (unconfigured test substitute) is the
/// fail-closed value, never <see cref="Sent"/>.
/// </summary>
public enum AlertDeliveryResult
{
    NoRecipientConfigured,
    Sent,
    Failed,

    /// <summary>
    /// A configured channel terminally refused the delivery (e.g. a Slack webhook answering
    /// 4xx — rotated URL, deleted channel), as distinct from a transient <see cref="Failed"/>
    /// (5xx, timeouts) that a later retry can fix. The claim is still not closed — the channel
    /// configuration may be repaired — but the log must not describe this as a transient failure.
    /// </summary>
    PermanentlyRejected,
}

public interface IAlertNotifier
{
    Task<AlertDeliveryResult> NotifyAsync(BudgetAlertPayload payload, CancellationToken ct = default);
}
