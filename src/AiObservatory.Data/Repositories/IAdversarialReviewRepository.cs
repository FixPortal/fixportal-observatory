using AiObservatory.Data.Entities;
using NodaTime;

namespace AiObservatory.Data.Repositories;

// Projection record properties are consumed by JSON serialization.
// ReSharper disable NotAccessedPositionalProperty.Global

public record AdversarialReviewStats(
    string Reviewer,
    string Model,
    string Role,
    int RunCount,
    decimal? AvgCostPerRun,
    double AvgIssuesRaised,
    double AvgIssuesAccepted,
    decimal? AvgCostPerAcceptedFinding,
    double AvgDurationMs,
    int UnknownCostRuns
);

public interface IAdversarialReviewRepository
{
    Task<(Guid Id, bool Existed)> RecordRunAsync(AdversarialReviewRun run, CancellationToken ct = default);
    Task<IReadOnlyList<AdversarialReviewRun>> GetRunsAsync(string? runId = null, CancellationToken ct = default);
    Task<IReadOnlyList<AdversarialReviewStats>> GetStatsAsync(CancellationToken ct = default);

    /// <summary>Runs recorded in [from, toExclusive), so a windowed caller does not read the whole history.</summary>
    Task<IReadOnlyList<AdversarialReviewRun>> GetRunsRecordedBetweenAsync(
        Instant from,
        Instant toExclusive,
        CancellationToken ct = default
    );

    Task<IReadOnlyList<AdversarialReviewFinding>> GetFindingsForRunsAsync(
        IReadOnlyCollection<string> runIds,
        CancellationToken ct = default
    );

    /// <returns>False when the run has no run rows (nothing is written).</returns>
    Task<bool> ReplaceFindingsAsync(
        string runId,
        IReadOnlyList<AdversarialReviewFinding> findings,
        CancellationToken ct = default
    );
    /// <param name="since">When set, only findings recorded at or after this instant.</param>
    Task<IReadOnlyList<AdversarialReviewFinding>> GetFindingsAsync(
        string? runId = null,
        CancellationToken ct = default,
        Instant? since = null
    );

    /// <returns>The repo of each given run that has one; runs without a repo are absent.</returns>
    Task<IReadOnlyDictionary<string, string>> GetRepositoriesByRunAsync(
        IReadOnlyCollection<string> runIds,
        CancellationToken ct = default
    );
    Task<int> DeleteAllRunsAsync(CancellationToken ct = default);
    Task<int> DeleteRunAsync(string runId, CancellationToken ct = default);
}
