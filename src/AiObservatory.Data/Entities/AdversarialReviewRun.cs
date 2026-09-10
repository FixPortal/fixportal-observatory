using NodaTime;

namespace AiObservatory.Data.Entities;

public sealed class AdversarialReviewRun
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Reviewer { get; init; } = "";
    public string Model { get; init; } = "";

    /// <summary>"reviewer" or "judge" — distinguishes the Opus judge from the Sonnet reviewer (both vendor "anthropic").</summary>
    public string Role { get; init; } = "reviewer";

    /// <summary>Repository the run reviewed (basename of the repo root). Null when not supplied.</summary>
    public string? Repo { get; init; }

    /// <summary>Operator-assigned run name shown as the dashboard card title. Null when the
    /// invocation gave no naming directive; capped at 80 chars.</summary>
    public string? Summary { get; init; }

    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public decimal CostUsd { get; init; }
    public long ReviewDurationMs { get; init; }
    public int IssuesRaised { get; init; }
    public int IssuesAccepted { get; init; }

    /// <summary>
    /// Null when IssuesAccepted is zero (guard divide-by-zero).
    /// </summary>
    public decimal? CostPerAcceptedFinding { get; init; }

    /// <summary>
    /// Number of chunks aggregated into this participant row when the run was a
    /// chunked/batched review (large diff split into cohesive chunks, each a full
    /// panel run, summed per participant). Null for a single-diff run.
    /// </summary>
    public int? ChunkCount { get; init; }

    /// <summary>
    /// Client-supplied utc-timestamp-slug used for idempotency.
    /// </summary>
    public string RunId { get; init; } = "";

    public Instant RecordedAt { get; init; }
}
