namespace AiObservatory.Audit.Contracts;

public sealed record ClosureEvidence(ClosureState State, DateTimeOffset? Timestamp, long? Sequence);

public enum ClosureState
{
    Unknown,
    Open,
    ExplicitlyClosed,
}
