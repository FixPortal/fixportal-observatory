using NodaTime;

namespace AiObservatory.Data.Entities;

/// <summary>One lossless financial fact reported by a provider.</summary>
public sealed class BillingObservation
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string ProviderKey { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
    public SourceKind SourceKind { get; set; } = SourceKind.ProviderApi;
    public UsageScope UsageScope { get; set; } = UsageScope.Unknown;
    public CostBasis CostBasis { get; set; } = CostBasis.Billed;
    public string ObservationKey { get; set; } = string.Empty;
    public LocalDate OccurredOn { get; set; }
    public string? BillingPeriod { get; set; }
    public string? Service { get; set; }
    public string? Sku { get; set; }
    public string Currency { get; set; } = "USD";
    public decimal GrossAmount { get; set; }
    public decimal CreditAmount { get; set; }
    public decimal NetAmount { get; set; }
    public string RawPayload { get; set; } = "{}";
    public Instant ObservedAt { get; set; }
}
