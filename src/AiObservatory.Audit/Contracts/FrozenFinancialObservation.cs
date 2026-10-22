namespace AiObservatory.Audit.Contracts;

/// <summary>Unqualified provider financial observations remain frozen facts, without claiming billing reconciliation support.</summary>
public sealed record FrozenFinancialObservation(
    Guid RowId,
    string SourceId,
    string Provider,
    string ObservationKey,
    DateOnly OccurredOn,
    string? PeriodId,
    ObservationProvenance Provenance,
    string Currency,
    decimal GrossAmount,
    decimal CreditAmount,
    decimal NetAmount,
    DateTimeOffset ObservedAt
);
