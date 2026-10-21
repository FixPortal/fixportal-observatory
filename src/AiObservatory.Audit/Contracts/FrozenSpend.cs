namespace AiObservatory.Audit.Contracts;

/// <summary>Refunds remain signed. Account/period/entry identities are opaque, with no bank or invoice content.</summary>
public sealed record FrozenSpend(
    Guid RowId,
    string SourceId,
    string? EntryKey,
    string? AccountId,
    string? PeriodId,
    Guid VendorId,
    Guid CategoryId,
    DateOnly OccurredOn,
    DateOnly? PeriodFrom,
    DateOnly? PeriodTo,
    ObservationProvenance Provenance,
    string Currency,
    decimal Amount,
    decimal AmountGbp,
    decimal FxRate,
    string? FxRateId,
    DateTimeOffset RecordedAt,
    DateTimeOffset ObservedAt,
    string RevisionDigest
);
