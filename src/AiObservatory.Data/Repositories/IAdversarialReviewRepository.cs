using AiObservatory.Data.Entities;

namespace AiObservatory.Data.Repositories;

// Projection record properties are consumed by JSON serialization.
// ReSharper disable NotAccessedPositionalProperty.Global

public record AdversarialReviewStats(
    string Reviewer,
    string Model,
    string Role,
    int RunCount,
    decimal AvgCostPerRun,
    double AvgIssuesRaised,
    double AvgIssuesAccepted,
    decimal? AvgCostPerAcceptedFinding,
    double AvgDurationMs
);

public interface IAdversarialReviewRepository
{
    Task<(Guid Id, bool Existed)> RecordRunAsync(AdversarialReviewRun run, CancellationToken ct = default);
    Task<IReadOnlyList<AdversarialReviewRun>> GetRunsAsync(string? runId = null, CancellationToken ct = default);
    Task<IReadOnlyList<AdversarialReviewStats>> GetStatsAsync(CancellationToken ct = default);
    Task<int> DeleteAllRunsAsync(CancellationToken ct = default);
    Task<int> DeleteRunAsync(string runId, CancellationToken ct = default);
}
