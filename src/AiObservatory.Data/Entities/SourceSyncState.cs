using NodaTime;

namespace AiObservatory.Data.Entities;

public sealed class SourceSyncState
{
    public string SourceId { get; init; } = "";
    public bool IsConfigured { get; set; }
    public bool? IsAvailable { get; set; }
    public long ExpectedRefreshIntervalSeconds { get; set; }
    public Instant? LastAttemptAt { get; set; }
    public Instant? LastSuccessAt { get; set; }
    public Instant? LatestObservationAt { get; set; }
    public LocalDate? PendingFromDate { get; set; }
    public int ConsecutiveFailureCount { get; set; }
    public string? LastError { get; set; }

    /// <summary>
    /// Sweep-status rows only: the files the machine's last sweep held back across all its sources,
    /// as the sweep reported it. Null for other sources and for a sweep that did not report it.
    /// </summary>
    public int? HeldFiles { get; set; }
}
