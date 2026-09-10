using NodaTime;

namespace AiObservatory.Ingest.Services.Copilot;

public sealed record CopilotDailyReportRecord(
    LocalDate Day,
    string OrganizationId,
    int? DailyActiveUsers,
    int? WeeklyActiveUsers,
    int? MonthlyActiveUsers,
    long UserInitiatedInteractionCount,
    long CodeGenerationActivityCount,
    long CodeAcceptanceActivityCount,
    string RawJson,
    Instant? ObservedAt
);
