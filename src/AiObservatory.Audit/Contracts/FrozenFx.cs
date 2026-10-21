namespace AiObservatory.Audit.Contracts;

public sealed record FrozenFx(
    string SourceId,
    string Id,
    string ContentHash,
    DateOnly Date,
    string FromCurrency,
    string ToCurrency,
    decimal Rate,
    DateTimeOffset ObservedAt
);
