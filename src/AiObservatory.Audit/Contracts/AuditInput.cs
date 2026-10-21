namespace AiObservatory.Audit.Contracts;

public sealed record AuditInput
{
    public required int SchemaVersion { get; init; }
    public required string VerifierVersion { get; init; }
    public required string RuleVersion { get; init; }
    public required AuditScope Scope { get; init; }
    public IReadOnlyList<CaptureManifest> Captures { get; init; } = [];
    public IReadOnlyList<SourceUsageRecord> Records { get; init; } = [];
    public IReadOnlyList<FrozenEvent> Events { get; init; } = [];
    public IReadOnlyList<FrozenAggregate> Aggregates { get; init; } = [];
    public IReadOnlyList<FrozenSpend> Spend { get; init; } = [];
    public IReadOnlyList<FrozenSourceStatus> SourceStatuses { get; init; } = [];
    public IReadOnlyList<FrozenPricing> PricingCatalogs { get; init; } = [];
    public IReadOnlyList<FrozenFx> FxRates { get; init; } = [];
    public FrozenPresentationProbe? PresentationProbe { get; init; }
    public AuditOptions Options { get; init; } = new();
    public IReadOnlyList<NamedTolerance> Tolerances { get; init; } = [];
}
