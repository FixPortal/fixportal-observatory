using System.Globalization;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using MimeKit;
using NodaTime;

namespace AiObservatory.Api.Services;

public class BudgetAlertService(
    IUsageRepository repository,
    IClock clock,
    IAlertNotifier notifier,
    ILogger<BudgetAlertService> logger,
    IConfiguration config
)
{
    // Message-Id domain. Defaults to the domain of the configured sender, which is the address
    // these alerts are actually sent from and therefore the right authority for the id. Falls
    // back to a neutral literal when no sender is configured, rather than to a maintainer domain
    // that a self-hoster would otherwise stamp on their own outgoing mail — this used to be a
    // hardcoded "observatory.fixportal.com", which was not even a domain this project serves.
    // Resolved once per service instance, not per delivery attempt, so every retry a given
    // process makes for a claim carries an identical Message-Id even if the underlying
    // configuration source is reloadable. Across a RESTART that also changes the sender or the
    // explicit domain, a still-undelivered claim would get a new Message-Id and the receiving
    // server could show the alert twice. That residual is accepted rather than closed by
    // persisting the domain on the claim: the delivery path already states it cannot be
    // exactly-once (see DeliverEmailAsync), the cost is one duplicate alert email in a window
    // that opens only when an operator changes mail configuration mid-flight, and the
    // alternative is a schema column and migration to defend it.
    private string? _messageIdDomain;

    private string MessageIdDomain() => _messageIdDomain ??= ResolveMessageIdDomain();

    private string ResolveMessageIdDomain()
    {
        var configured = config["BUDGET_ALERT_MESSAGE_ID_DOMAIN"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }
        const string fallback = "observatory.local";
        // Blank-but-set falls through to the next source: `??` only sees null, so an empty
        // BUDGET_ALERT_EMAIL_FROM would otherwise shadow a perfectly good SMTP user.
        var sender = config["BUDGET_ALERT_EMAIL_FROM"];
        if (string.IsNullOrWhiteSpace(sender))
        {
            sender = config["BUDGET_ALERT_SMTP_USER"];
        }
        if (string.IsNullOrWhiteSpace(sender))
        {
            return fallback;
        }

        // Parse as a mailbox rather than slicing from the last '@': a display-name sender
        // ("Alerts <alerts@example.com>") must yield "example.com", not "example.com>", and a
        // quoted local part may legally contain an '@' of its own. MimeKit's lenient default
        // also accepts a bare word as a local-part-only mailbox with no domain, so a sender
        // with no parseable domain still falls back rather than emitting "budget-alert-{id}@".
        if (!MailboxAddress.TryParse(sender, out var mailbox) || mailbox.Domain.Length == 0)
        {
            return fallback;
        }

        return mailbox.Domain;
    }

    // virtual to match the other de-interfaced services (FxRateProvider, AnthropicIntelligenceClient):
    // overridable for subclass-mocking now that IBudgetAlertService is gone.
    public virtual async Task CheckAndAlertAsync(CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        var rules = await repository.GetBudgetRulesAsync(ct);
        var today = now.InUtc().Date;
        var yesterday = today.PlusDays(-1);

        var monthStart = new LocalDate(today.Year, today.Month, 1);

        foreach (var rule in rules)
        {
            if (rule.Period == BillingPeriod.Daily)
            {
                await CheckDailyRuleSafelyAsync(rule, yesterday, now, ct);
                continue;
            }

            if (AlreadyFired(rule, today, yesterday, monthStart))
            {
                continue;
            }

            var (from, to) = GetWindow(rule.Period, today, yesterday, monthStart);
            if (rule.EvaluationStartsOn > to)
            {
                continue;
            }

            if (rule.EvaluationStartsOn > from)
            {
                from = rule.EvaluationStartsOn;
            }

            await CheckRuleSafelyAsync(rule, from, to, now, ct);
        }

        var deliveryStartedAt = clock.GetCurrentInstant();
        foreach (
            var pending in await repository.GetDeliverableBudgetAlertEmailsAsync(
                deliveryStartedAt.Minus(BudgetAlertEmailLease.Duration),
                deliveryStartedAt.Minus(MaxDeliveryAge),
                ct
            )
        )
        {
            await DeliverEmailAsync(pending, ct);
        }
    }

    // Claims older than this leave the delivery set. LastTriggeredAt is a claim-CREATION stamp,
    // not a statement about which dates have been scanned, so it cannot bound delivery either —
    // but unbounded, a claim that can never deliver (no channel configured, a terminally dead
    // webhook) is re-leased and re-warned on every pass forever, starving newer claims once the
    // batch size is reached and bursting every historical alert at an operator who configures a
    // channel months later. The claim is NOT closed: it stays pending, so a channel configured
    // inside the window still receives the alert — the bound only stops selecting stale ones.
    private static readonly Duration MaxDeliveryAge = Duration.FromDays(30);

    // The daily rescan runs from the rule's evaluation boundary on every run — there is no
    // watermark. LastTriggeredAt stamps claim creation, not "dates scanned so far": spend lands
    // behind any earlier day (a backdated manual entry, or GitHub billing rows pinned to the
    // month start and re-upserted with a rising amount all month), so a trailing window behind
    // the last trigger permanently drops those days once any claim fires more than the window
    // after them. The scan is one grouped SQL query and GetOrCreateBudgetAlertAsync is
    // idempotent per (rule, period), so re-reading the rule's whole lifetime is cheap and can
    // never double-alert.
    private async Task CheckDailyRuleSafelyAsync(BudgetRule rule, LocalDate through, Instant now, CancellationToken ct)
    {
        var from = rule.EvaluationStartsOn;
        if (from > through)
        {
            return;
        }

        IReadOnlyList<DailyBilledSpend> dailySpend;
        try
        {
            // One grouped SQL query covers every completed day since the lower bound.
            // Missing/zero days cannot exceed the positive rule threshold.
            dailySpend = await repository.GetDailyBilledSpendGbpAsync(from, through, rule.Provider, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Budget alert check failed for rule {RuleId} ({Period})", rule.Id, rule.Period);
            return;
        }

        foreach (var day in dailySpend)
        {
            try
            {
                await CreateAlertAsync(rule, day.Date, day.Date, day.AmountGbp, now, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Budget alert check failed for rule {RuleId} ({Period}) on {Date}",
                    rule.Id,
                    rule.Period,
                    day.Date
                );
            }
        }
    }

    private async Task CheckRuleSafelyAsync(
        BudgetRule rule,
        LocalDate from,
        LocalDate to,
        Instant now,
        CancellationToken ct
    )
    {
        try
        {
            await CheckRuleAsync(rule, from, to, now, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One rule/window must not abort sibling rules or the remaining catch-up days.
            logger.LogError(ex, "Budget alert check failed for rule {RuleId} ({Period})", rule.Id, rule.Period);
        }
    }

    private static (LocalDate From, LocalDate To) GetWindow(
        BillingPeriod period,
        LocalDate today,
        LocalDate yesterday,
        LocalDate monthStart
    ) =>
        // Daily uses yesterday, the last completed day. At the worker's UTC-midnight
        // run, today's spend is approximately zero.
        period switch
        {
            BillingPeriod.Daily => (yesterday, yesterday),
            BillingPeriod.Weekly => (today.PlusDays(-6), today),
            BillingPeriod.Monthly => (monthStart, today),
            _ => (yesterday, yesterday),
        };

    private static bool AlreadyFired(BudgetRule rule, LocalDate today, LocalDate yesterday, LocalDate monthStart)
    {
        if (!rule.LastTriggeredAt.HasValue)
        {
            return false;
        }

        var lastDate = rule.LastTriggeredAt.Value.InUtc().Date;
        return rule.Period switch
        {
            BillingPeriod.Daily => lastDate >= yesterday,
            BillingPeriod.Weekly => lastDate >= today.PlusDays(-6),
            BillingPeriod.Monthly => lastDate >= monthStart,
            _ => false,
        };
    }

    private async Task CheckRuleAsync(BudgetRule rule, LocalDate from, LocalDate to, Instant now, CancellationToken ct)
    {
        var totalSpendGbp = await repository.GetBilledSpendGbpAsync(from, to, rule.Provider, ct);
        await CreateAlertAsync(rule, from, to, totalSpendGbp, now, ct);
    }

    private async Task CreateAlertAsync(
        BudgetRule rule,
        LocalDate from,
        LocalDate to,
        decimal totalSpendGbp,
        Instant now,
        CancellationToken ct
    )
    {
        if (totalSpendGbp <= rule.ThresholdGbp)
        {
            return;
        }

        var insight = new Insight
        {
            GeneratedAt = now,
            PeriodStart = from,
            PeriodEnd = to,
            InsightType = InsightType.BudgetAlert,
            Title = string.Create(
                CultureInfo.InvariantCulture,
                $"Budget alert: {rule.Period} billed spend exceeded £{rule.ThresholdGbp:F2}"
            ),
            Body = string.Create(
                CultureInfo.InvariantCulture,
                $"Total {rule.Period.ToString().ToLowerInvariant()} billed spend reached £{totalSpendGbp:F2}, exceeding your £{rule.ThresholdGbp:F2} threshold."
            ),
            Data = System.Text.Json.JsonSerializer.Serialize(
                new { thresholdGbp = rule.ThresholdGbp, actualSpendGbp = totalSpendGbp }
            ),
        };

        await repository.GetOrCreateBudgetAlertAsync(
            rule.Id,
            from,
            to,
            rule.ThresholdGbp,
            totalSpendGbp,
            insight,
            now,
            ct
        );
    }

    private async Task DeliverEmailAsync(BudgetAlertEmail email, CancellationToken ct)
    {
        var acquiredAt = clock.GetCurrentInstant();
        var leaseId = Guid.NewGuid();
        if (
            !await repository.TryAcquireBudgetAlertEmailLeaseAsync(
                email.ClaimId,
                leaseId,
                acquiredAt,
                acquiredAt.Minus(BudgetAlertEmailLease.Duration),
                ct
            )
        )
        {
            return;
        }

        var payload = new BudgetAlertPayload(
            email.Provider?.ToString() ?? "all",
            email.Period.ToString(),
            email.ThresholdGbp,
            email.ActualSpendGbp,
            email.CreatedAt.ToDateTimeOffset(),
            $"budget-alert-{email.ClaimId:N}@{MessageIdDomain()}",
            email.ClaimId
        );

        try
        {
            // At-least-once attempt semantics: retries reuse a stable Message-Id whose
            // identifying half — the ClaimId — comes from the durable claim. Its domain half is
            // configuration resolved once per process, so it is stable for every retry this
            // process makes but not across a restart that also changes mail configuration.
            // SMTP success followed by a lost acknowledgement can duplicate delivery anyway;
            // the protocol cannot make that outcome exactly once.
            var result = await notifier.NotifyAsync(payload, ct);
            if (result == AlertDeliveryResult.Sent)
            {
                await repository.MarkBudgetAlertEmailSentAsync(email.ClaimId, leaseId, clock.GetCurrentInstant(), ct);
                return;
            }

            // No channel actually delivered. The claim is deliberately NOT closed: notification
            // settings are runtime-editable, so the lease is released and the claim stays
            // pending — but only within MaxDeliveryAge, after which the deliverable query stops
            // selecting it. The outcome distinguishes why: NoRecipientConfigured (nothing set
            // up), PermanentlyRejected (a channel terminally refused, e.g. a rotated Slack
            // webhook's 4xx), or Failed (transient — 5xx/timeouts, the only kind retry can fix).
            await repository.ReleaseBudgetAlertEmailLeaseAsync(email.ClaimId, leaseId, ct);
            logger.LogWarning(
                "Budget alert email for rule {RuleId} was not delivered on any channel ({Outcome}); its lease was released so the claim stays pending and retries",
                email.RuleId,
                result
            );
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            logger.LogWarning(
                ex,
                "Budget alert email for rule {RuleId} was interrupted; its lease will recover after {LeaseMinutes} minutes and retry with the same Message-Id. Delivery may be duplicated if SMTP accepted it",
                email.RuleId,
                BudgetAlertEmailLease.Duration.TotalMinutes
            );
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Budget alert email for rule {RuleId} failed; releasing its lease for retry with the same Message-Id. Delivery may be duplicated if SMTP accepted it",
                email.RuleId
            );
            await repository.ReleaseBudgetAlertEmailLeaseAsync(email.ClaimId, leaseId, ct);
        }
    }
}
