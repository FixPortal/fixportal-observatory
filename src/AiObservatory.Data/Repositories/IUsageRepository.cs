using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing;
using NodaTime;

namespace AiObservatory.Data.Repositories;

// Projection record properties are consumed by JSON serialization.
// ReSharper disable NotAccessedPositionalProperty.Global

/// <summary>
/// Outcome of <see cref="IUsageRepository.RecordEventAsync"/>.
/// </summary>
public enum RecordEventDisposition
{
    Created,
    Unchanged,
    Corrected,
}

public sealed record RecordEventResult(Guid EventId, RecordEventDisposition Disposition, bool WatermarkAdvanced = false)
{
    public bool IsDuplicate => Disposition == RecordEventDisposition.Unchanged;
}

/// <summary>
/// Outcome of <see cref="IUsageRepository.PurgeProviderAsync"/>: how many raw events
/// and pre-aggregated daily rows were removed for the provider.
/// </summary>
public sealed record PurgeResult(int DeletedEvents, int DeletedAggregates);

/// <summary>
/// Outcome of <see cref="IUsageRepository.PatchEventCostAsync"/>: the old and new cost
/// for the updated event. <see cref="OldCostUsd"/> is null when the event was previously
/// unpriced (unknown), which is distinct from a known zero. Null result when no event
/// with the given key exists.
/// </summary>
public sealed record PatchEventCostResult(Guid EventId, decimal? OldCostUsd, decimal NewCostUsd);

/// <summary>
/// Minimal projection of a <see cref="AiObservatory.Data.Entities.UsageEvent"/> for cost-correction use.
/// <see cref="EventKey"/> is projected in its stored form (legacy-api rows carry the
/// <c>"{Provider}:"</c> prefix); feeding it back to <see cref="IUsageRepository.PatchEventCostAsync"/>
/// together with <see cref="SourceId"/> addresses the same row.
/// </summary>
public sealed record EventCostRecord(
    Guid Id,
    string SourceId,
    string? EventKey,
    string? Runtime,
    string? SessionId,
    string? AgentId,
    string? Model,
    long InputTokens,
    long OutputTokens,
    long? CacheWriteTokens,
    long? ThoughtTokens,
    decimal? CostUsd
);

/// <summary>
/// Canonical fields needed by a local collector to reconcile its source-scoped snapshots.
/// Raw evidence is deliberately excluded, and so is the cost figure itself: only whether a
/// cost is known (<see cref="HasCost"/>) is exposed, expressed in the type rather than as a
/// magic zero.
/// </summary>
public sealed record LocalSnapshotRecord(
    Provider Provider,
    Instant OccurredAtUtc,
    string? Model,
    bool HasCost,
    string? Runtime,
    string SourceId,
    SourceKind SourceKind,
    UsageScope UsageScope,
    CostBasis CostBasis,
    string EventKey
);

public sealed record BudgetAlertClaimResult(
    Guid ClaimId,
    bool Created,
    decimal ThresholdGbp,
    decimal ActualSpendGbp,
    Instant CreatedAt
);

public sealed record BudgetAlertEmail(
    Guid ClaimId,
    Guid RuleId,
    Provider? Provider,
    BillingPeriod Period,
    LocalDate PeriodStart,
    LocalDate PeriodEnd,
    decimal ThresholdGbp,
    decimal ActualSpendGbp,
    Instant CreatedAt
);

public sealed record DailyBilledSpend(LocalDate Date, decimal AmountGbp);

public interface IUsageRepository
{
    Task<RecordEventResult> RecordEventAsync(UsageEvent evt, CancellationToken ct = default);

    /// <summary>
    /// Resolves and records list-price or notional usage under the provider pricing activation lock.
    /// </summary>
    Task<RecordEventResult> RecordEstimatedEventAsync(UsageEvent evt, CancellationToken ct = default);

    /// <summary>
    /// Replaces pricing on an eligible estimated event and applies the signed aggregate delta.
    /// Joins the current database transaction when activation is in progress.
    /// </summary>
    /// <param name="priced">
    /// The event as it was read when <paramref name="quote"/> was calculated. The write is skipped
    /// if the locked row's pricing inputs or cost figures have since moved, so a quote is never
    /// applied to an event it was not calculated from.
    /// </param>
    /// <param name="quote">The price calculated from <paramref name="priced"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateEventPricingAsync(UsageEvent priced, UsagePriceQuote? quote, CancellationToken ct = default);

    /// <summary>
    /// Deletes ALL usage data for one provider — both the raw <c>UsageEvents</c> and the
    /// pre-aggregated <c>DailyAggregates</c> (which are maintained additively, so deleting
    /// events alone would leave the aggregates stale). Used to reset a provider before a
    /// clean backfill. Both deletes run in one transaction.
    /// </summary>
    Task<PurgeResult> PurgeProviderAsync(Provider provider, CancellationToken ct = default);

    Task<IReadOnlyList<DailyAggregate>> GetAggregatesAsync(
        LocalDate from,
        LocalDate to,
        CancellationToken ct = default
    );

    Task<decimal> GetBilledSpendGbpAsync(
        LocalDate from,
        LocalDate to,
        Provider? provider = null,
        CancellationToken ct = default
    );

    Task<IReadOnlyList<DailyBilledSpend>> GetDailyBilledSpendGbpAsync(
        LocalDate from,
        LocalDate to,
        Provider? provider = null,
        CancellationToken ct = default
    );

    Task<IReadOnlyList<BudgetRule>> GetBudgetRulesAsync(CancellationToken ct = default);

    Task<BudgetAlertClaimResult> GetOrCreateBudgetAlertAsync(
        Guid ruleId,
        LocalDate periodStart,
        LocalDate periodEnd,
        decimal thresholdGbp,
        decimal actualSpendGbp,
        Insight insight,
        Instant triggeredAt,
        CancellationToken ct = default
    );

    Task<IReadOnlyList<BudgetAlertEmail>> GetDeliverableBudgetAlertEmailsAsync(
        Instant leaseExpiredBefore,
        Instant createdOnOrAfter,
        CancellationToken ct = default
    );

    Task<bool> TryAcquireBudgetAlertEmailLeaseAsync(
        Guid claimId,
        Guid leaseId,
        Instant acquiredAt,
        Instant leaseExpiredBefore,
        CancellationToken ct = default
    );

    Task ReleaseBudgetAlertEmailLeaseAsync(Guid claimId, Guid leaseId, CancellationToken ct = default);

    Task MarkBudgetAlertEmailSentAsync(Guid claimId, Guid leaseId, Instant sentAt, CancellationToken ct = default);

    Task<bool> GetBudgetAlertSlackSentAsync(Guid claimId, CancellationToken ct = default);

    Task MarkBudgetAlertSlackSentAsync(Guid claimId, Instant at, CancellationToken ct = default);

    Task AddInsightAsync(Insight insight, CancellationToken ct = default);
    Task<IReadOnlyList<Insight>> GetUnacknowledgedInsightsAsync(CancellationToken ct = default);
    Task AcknowledgeInsightAsync(Guid insightId, Instant at, CancellationToken ct = default);
    Task<LocalDate?> GetLatestInsightPeriodEndAsync(CancellationToken ct = default);

    Task<NotificationSettings?> GetNotificationSettingsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<Subscription>> GetActiveSubscriptionsAsync(LocalDate today, CancellationToken ct = default);

    /// <summary>
    /// Updates <c>CostUsd</c> on the event identified by <paramref name="provider"/> +
    /// <paramref name="sourceId"/> + <paramref name="eventKey"/> and adjusts its
    /// DailyAggregate atomically. Returns null when no event with that identity exists.
    /// <paramref name="eventKey"/> may be given in either the unprefixed or the stored
    /// (<c>"{Provider}:"</c>-prefixed, legacy-api only) form. A correction moves an
    /// estimated (<see cref="CostBasis.ListPriceEstimate"/>/<see cref="CostBasis.Notional"/>)
    /// event to <see cref="CostBasis.ProviderEstimated"/> and stamps <c>CorrectedAt</c>, so
    /// neither the repricing pass nor a cost-less snapshot replay can silently revert it.
    /// The rebase happens even when the corrected figure equals the stored one — the operator
    /// has taken authority over the figure either way.
    /// </summary>
    /// <param name="newCacheSavingsUsd">
    /// The cache-savings figure to store alongside the corrected cost, or null to keep the
    /// event's current value. Pass an explicit figure (0 when the correction has no savings to
    /// report) so the aggregate's unknown-savings count clears: the rebase removes the row from
    /// the repricer's scan, so a null left behind is never repaired.
    /// </param>
    Task<PatchEventCostResult?> PatchEventCostAsync(
        Provider provider,
        string sourceId,
        string eventKey,
        decimal newCostUsd,
        decimal? newCacheSavingsUsd = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// Returns raw events for <paramref name="provider"/>, ordered by <c>OccurredAt</c>,
    /// projected to the minimal fields needed for cost-correction backfill. Optionally
    /// scoped to an <c>OccurredAt</c> window (<paramref name="from"/>/<paramref name="to"/>),
    /// and always capped at <paramref name="limit"/> rows so a high-volume provider cannot
    /// be dumped unbounded in one request — page by date window for more.
    /// </summary>
    Task<IReadOnlyList<EventCostRecord>> GetEventsByProviderAsync(
        Provider provider,
        Instant? from = null,
        Instant? to = null,
        int limit = 10_000,
        CancellationToken ct = default
    );

    Task<IReadOnlyList<LocalSnapshotRecord>> GetLocalSnapshotsAsync(string sourceId, CancellationToken ct = default);
}
