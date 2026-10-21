namespace AiObservatory.Audit.Contracts;

/// <summary>Allowlisted source facts; IDs are opaque installation-local tokens, never filesystem paths.</summary>
public sealed record SourceUsageRecord
{
    public required Guid MachineId { get; init; }
    public required string SourceId { get; init; }
    public required string SourceFormat { get; init; }
    public required string SourceVersion { get; init; }
    public required SourceTool Tool { get; init; }
    public required UsageRecordKind Kind { get; init; }
    public required string Generation { get; init; }
    public required string FileId { get; init; }
    public string? SessionId { get; init; }
    public string? MessageId { get; init; }
    public string? AgentId { get; init; }
    public string? ParentId { get; init; }
    public required long Sequence { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public string? Model { get; init; }
    public string? Provider { get; init; }
    public SourceCounters? Counters { get; init; }
    public decimal? Cost { get; init; }
    public string? Currency { get; init; }
    public required PricingDimensions Pricing { get; init; }
    public required ClosureEvidence Closure { get; init; }
}
