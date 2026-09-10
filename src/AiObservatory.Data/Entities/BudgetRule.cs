using NodaTime;

namespace AiObservatory.Data.Entities;

public sealed class BudgetRule
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Provider? Provider { get; set; }
    public BillingPeriod Period { get; init; }
    public decimal ThresholdGbp { get; init; }
    public LocalDate EvaluationStartsOn { get; init; }
    public Instant? LastTriggeredAt { get; set; }
}
