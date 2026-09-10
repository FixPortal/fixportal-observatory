using NodaTime;

namespace AiObservatory.Data.Entities;

public sealed class PricingSnapshot
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Provider Provider { get; init; }
    public string SourceId { get; init; } = "";
    public Instant RetrievedAt { get; init; }
    public string SourceUrl { get; init; } = "";
    public string ContentHash { get; init; } = "";
    public string RawEvidence { get; init; } = "";
    public string NormalizedCatalog { get; init; } = "";
    public bool IsActive { get; set; }
}
