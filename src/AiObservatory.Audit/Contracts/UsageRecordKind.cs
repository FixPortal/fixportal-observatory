namespace AiObservatory.Audit.Contracts;

public enum UsageRecordKind
{
    MessageUsage,
    CumulativeUsage,
    Baseline,
    SessionMetadata,
    SessionClosure,
    Reset,
}
