namespace AiObservatory.Api.Services;

/// <summary>
/// One rendered notification, ready for any transport. The caller renders; the notifiers only
/// deliver. That split is what lets a second kind of alert — the daily source-health digest —
/// reach every configured channel without each transport growing a second message template.
/// <para>
/// Durable deliveries have at-least-once attempt semantics. Retries preserve
/// <see cref="MessageId"/>, but SMTP can accept a message before the sender observes failure,
/// so recipients may still see a duplicate. No exactly-once claim is made.
/// </para>
/// </summary>
/// <param name="Subject">Email subject, and the bolded first line of a Slack post.</param>
/// <param name="Body">Plain text. Rendered as-is by both channels.</param>
/// <param name="MessageId">
/// RFC 5322 Message-Id for retry collapsing at the receiving server, or null to let MimeKit
/// generate one. Null suits a fire-and-forget alert that is never retried under its own
/// identity: a stable id there would only invite a receiving server to hide a later send.
/// </param>
/// <param name="SlackFenceClaimId">
/// The budget-alert claim whose <c>SlackSentAt</c> fences Slack delivery to once per claim,
/// or null when the caller fences itself. The digest does the latter — it holds a per-day
/// claim — so it must not be fenced a second time against a claim row it does not own.
/// </param>
public sealed record AlertMessage(
    string Subject,
    string Body,
    string? MessageId = null,
    Guid? SlackFenceClaimId = null
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
    Task<AlertDeliveryResult> NotifyAsync(AlertMessage alert, CancellationToken ct = default);
}
