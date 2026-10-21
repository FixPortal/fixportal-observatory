using AiObservatory.Data.Entities;
using NodaTime;

namespace AiObservatory.Data.Repositories;

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

public interface IBudgetAlertRepository
{
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
}
