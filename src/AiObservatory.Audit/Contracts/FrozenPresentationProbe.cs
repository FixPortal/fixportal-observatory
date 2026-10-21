namespace AiObservatory.Audit.Contracts;

/// <summary>Declared observations from a particular frontend build, not external attestation.</summary>
public sealed record FrozenPresentationProbe(
    string FrozenSnapshotHash,
    string FrontendBuildId,
    string FrontendSourceId,
    string SummaryInputDigest,
    IReadOnlyList<PresentationSummaryValue> Values
);

public sealed record PresentationSummaryValue(
    string Field,
    string GroupKey,
    decimal? UnroundedValue,
    string? FormattedValue,
    string Currency
);
