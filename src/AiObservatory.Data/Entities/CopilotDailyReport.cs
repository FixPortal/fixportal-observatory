using NodaTime;

namespace AiObservatory.Data.Entities;

/// <summary>One normalized day from a GitHub Copilot organization report.</summary>
public sealed class CopilotDailyReport
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public LocalDate Day { get; set; }
    public string SourceId { get; set; } = UsageSourceIds.CopilotOrgReport;
    public SourceKind SourceKind { get; set; } = SourceKind.ProviderApi;
    public UsageScope UsageScope { get; set; } = UsageScope.Subscription;
    public CostBasis CostBasis { get; set; } = CostBasis.None;
    public string ReportKey { get; set; } = string.Empty;
    public int? DailyActiveUsers { get; set; }
    public int? WeeklyActiveUsers { get; set; }
    public int? MonthlyActiveUsers { get; set; }
    public long UserInitiatedInteractionCount { get; set; }
    public long CodeGenerationActivityCount { get; set; }
    public long CodeAcceptanceActivityCount { get; set; }
    public string RawPayload { get; set; } = "{}";
    public Instant ObservedAt { get; set; }
}
