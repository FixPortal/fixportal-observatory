namespace AiObservatory.Api.Services.Suggestions;

/// <summary>Thresholds for the suggestion rules, bound from the <c>Suggestions</c> configuration section.</summary>
public sealed class SuggestionOptions
{
    public const string SectionName = "Suggestions";

    /// <summary>
    /// Whether the daily worker runs. On by default; the integration-test host turns it off so a
    /// startup evaluation (which fetches a live FX rate) cannot add traffic to unrelated tests.
    /// </summary>
    public bool WorkerEnabled { get; set; } = true;

    public int SeatWindowDays { get; set; } = 60;
    public int MinDetailedRuns { get; set; } = 5;
    public int MinSettledFindings { get; set; } = 10;
    public double FalsePositiveThreshold { get; set; } = 0.5;
    public int MinCatchesForConfirmationOnly { get; set; } = 10;
    public decimal CostOutlierMultiple { get; set; } = 3;
    public int CostOutlierMinPeerRuns { get; set; } = 3;
    public int CostOutlierMinPeers { get; set; } = 3;
    public int PlanWindowDays { get; set; } = 30;
    public double PlanUsageRatio { get; set; } = 0.5;
    public int PlanMinKnownCostRequests { get; set; } = 10;
    public int PlanMinActiveDays { get; set; } = 14;
    public double ReopenChangeFraction { get; set; } = 0.25;

    /// <summary>Returns a message naming the first invalid key, or null when every value is usable.</summary>
    public string? Validate()
    {
        var positive = new (string Key, double Value)[]
        {
            (nameof(SeatWindowDays), SeatWindowDays),
            (nameof(MinDetailedRuns), MinDetailedRuns),
            (nameof(MinSettledFindings), MinSettledFindings),
            (nameof(MinCatchesForConfirmationOnly), MinCatchesForConfirmationOnly),
            (nameof(CostOutlierMinPeerRuns), CostOutlierMinPeerRuns),
            (nameof(CostOutlierMinPeers), CostOutlierMinPeers),
            (nameof(PlanWindowDays), PlanWindowDays),
            (nameof(PlanMinKnownCostRequests), PlanMinKnownCostRequests),
            (nameof(PlanMinActiveDays), PlanMinActiveDays),
            (nameof(CostOutlierMultiple), (double)CostOutlierMultiple),
        };
        foreach (var (key, value) in positive)
        {
            if (value <= 0)
            {
                return $"{SectionName}:{key} must be greater than zero";
            }
        }

        var fractions = new (string Key, double Value)[]
        {
            (nameof(FalsePositiveThreshold), FalsePositiveThreshold),
            (nameof(PlanUsageRatio), PlanUsageRatio),
            (nameof(ReopenChangeFraction), ReopenChangeFraction),
        };
        foreach (var (key, value) in fractions)
        {
            if (value is <= 0 or > 1)
            {
                return $"{SectionName}:{key} must be greater than zero and at most one";
            }
        }
        return null;
    }
}
