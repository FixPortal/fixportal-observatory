using NodaTime;

namespace AiObservatory.Ingest.Sources;

public interface IUsageSource
{
    string SourceId { get; }

    Task<SourceIngestionResult> IngestAsync(LocalDate from, LocalDate through, CancellationToken cancellationToken);
}

// FailedRepoCount: how many per-repo lanes failed on an otherwise completed cycle.
// Zero for single-lane sources and total failures (those throw instead); the worker
// persists any non-zero count as a degraded state rather than unconditional success.
public sealed record SourceIngestionResult(Instant? LatestObservationAt, int FailedRepoCount = 0);

public sealed record SourceDefinition(string SourceId, bool IsConfigured, Duration ExpectedRefreshInterval);

public sealed class SourceUnavailableException(string message) : Exception(message);
