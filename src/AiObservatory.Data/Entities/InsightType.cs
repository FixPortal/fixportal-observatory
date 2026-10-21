namespace AiObservatory.Data.Entities;

// BudgetAlert is distinct from the AI-analysis types so budget-threshold insights can be
// excluded from the daily-analysis watermark (they carry PeriodEnd = today, which would
// otherwise advance the watermark past the day and permanently skip that day's analysis).
public enum InsightType
{
    Summary,
    Efficiency,
    Anomaly,
    Recommendation,
    BudgetAlert,
}
