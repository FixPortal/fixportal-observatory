using NodaTime;

namespace AiObservatory.Data.Entities;

public sealed class Subscription
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Provider Provider { get; set; }
    public string Name { get; set; } = "";
    public decimal CostAmount { get; set; }
    public string Currency { get; set; } = "GBP";
    public SubscriptionBillingInterval BillingInterval { get; set; } = SubscriptionBillingInterval.Monthly;
    public int? BillingMonth { get; set; }
    public int BillingDay { get; set; }
    public LocalDate ActiveFrom { get; set; }
    public LocalDate? ActiveTo { get; set; }
    public decimal? ExtraUsageCost { get; set; }
}
