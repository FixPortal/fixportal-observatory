namespace AiObservatory.Audit.Contracts;

/// <summary>Null counters mean an inspected raw observation was absent or unusable, never zero. A missing whole observation means it was not frozen.</summary>
public sealed record CacheDurationObservation(long? FiveMinuteTokens, long? OneHourTokens);
