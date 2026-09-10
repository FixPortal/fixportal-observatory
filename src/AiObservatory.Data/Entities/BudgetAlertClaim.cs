using NodaTime;

namespace AiObservatory.Data.Entities;

public sealed class BudgetAlertClaim
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid BudgetRuleId { get; init; }
    public LocalDate PeriodStart { get; init; }
    public LocalDate PeriodEnd { get; init; }
    public Guid InsightId { get; init; }
    public decimal ThresholdGbp { get; init; }
    public decimal ActualSpendGbp { get; init; }
    public Instant CreatedAt { get; init; }
    public Guid? EmailLeaseId { get; set; }
    public Instant? EmailLeaseAcquiredAt { get; set; }
    public Instant? EmailSentAt { get; set; }
    public Instant? SlackSentAt { get; set; }
}
