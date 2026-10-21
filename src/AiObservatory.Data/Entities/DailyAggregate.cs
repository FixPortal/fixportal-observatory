using NodaTime;

namespace AiObservatory.Data.Entities;

public sealed class DailyAggregate
{
    public LocalDate Date { get; init; }
    public Provider Provider { get; init; }
    public string Model { get; set; } = "";
    public string SourceId { get; set; } = UsageSourceIds.LegacyApi;
    public SourceKind SourceKind { get; set; } = SourceKind.Legacy;
    public UsageScope UsageScope { get; set; } = UsageScope.Unknown;
    public CostBasis CostBasis { get; set; } = CostBasis.Unknown;
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheWriteTokens { get; set; }

    /// <summary>The one-hour-TTL subset of <see cref="CacheWriteTokens"/>.</summary>
    // ReSharper disable once InconsistentNaming
    public long CacheWrite1hTokens { get; set; }

    public decimal CostUsd { get; set; }
    public int UnknownCostCount { get; set; }
    public decimal CacheSavingsUsd { get; set; }
    public int UnknownCacheSavingsCount { get; set; }
    public int RequestCount { get; set; }
}
