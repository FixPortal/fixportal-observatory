using NodaTime;

namespace AiObservatory.Data.Entities;

public sealed class UsageEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Provider Provider { get; set; }
    public Instant OccurredAt { get; set; }
    public Instant IngestedAt { get; init; }
    public string? Model { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long? CacheReadTokens { get; set; }
    public long? CacheWriteTokens { get; set; }

    /// <summary>
    /// The one-hour-TTL subset of <see cref="CacheWriteTokens"/>; the remainder is
    /// five-minute. Anthropic bills the two at different multiples of base input (2x vs
    /// 1.25x), so the split is what makes the cache-write line cost correctly. Null means
    /// the producer reported no breakdown, which prices as all-five-minute.
    /// </summary>
    // ReSharper disable once InconsistentNaming
    public long? CacheWrite1hTokens { get; set; }

    public long? ThoughtTokens { get; set; }
    public decimal? CostUsd { get; set; }
    public decimal? CacheSavingsUsd { get; set; }
    public string? Runtime { get; set; }
    public string? SessionId { get; set; }
    public string? AgentId { get; set; }
    public string RawPayload { get; set; } = "{}";
    public string SourceId { get; set; } = UsageSourceIds.LegacyApi;
    public SourceKind SourceKind { get; set; } = SourceKind.Legacy;
    public UsageScope UsageScope { get; set; } = UsageScope.Unknown;
    public CostBasis CostBasis { get; set; } = CostBasis.Unknown;
    public Instant ObservedAt { get; set; }

    /// <summary>
    /// Set when an operator manually corrected <see cref="CostUsd"/> via
    /// <c>PatchEventCostAsync</c>. While set, a replay that carries no cost of its own
    /// (the local sweepers re-post every snapshot with <c>costUsd: null</c>) updates the
    /// usage fields but must not roll the corrected figure back to unknown, and a source
    /// post carrying an explicit cost re-asserts authority by clearing the marker.
    /// </summary>
    public Instant? CorrectedAt { get; set; }

    /// <summary>
    /// Optional source-scoped snapshot key. Repeated identical submissions are no-ops;
    /// changed submissions replace the stored event and repair its aggregate atomically.
    /// </summary>
    public string? EventKey { get; init; }
}
