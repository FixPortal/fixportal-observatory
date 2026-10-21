using NodaTime;

namespace AiObservatory.Data.Entities;

public sealed class Insight
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Instant GeneratedAt { get; init; }
    public LocalDate PeriodStart { get; init; }
    public LocalDate PeriodEnd { get; init; }
    public InsightType InsightType { get; init; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string Data { get; set; } = "{}";
    public Instant? AcknowledgedAt { get; set; }
}
