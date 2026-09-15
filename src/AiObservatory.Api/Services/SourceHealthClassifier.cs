using AiObservatory.Data.Entities;
using NodaTime;

namespace AiObservatory.Api.Services;

/// <summary>
/// The single definition of what a source's sync state MEANS. Both
/// <c>/api/sources/status</c> and the daily source-health digest read it from here, so the
/// dashboard and the alert can never disagree about whether a source is degraded — a second
/// copy of these rules is exactly how an alert starts contradicting the screen it is meant
/// to summarise.
/// </summary>
public static class SourceHealthClassifier
{
    public const string NotConfigured = "notConfigured";
    public const string Unavailable = "unavailable";
    public const string Failing = "failing";
    public const string Configured = "configured";
    public const string Stale = "stale";
    public const string Fresh = "fresh";

    /// <summary>
    /// Precedence matters and is asserted by SourceStatusEndpointsTests: an unconfigured
    /// source is never "failing", and an unavailable one is never merely "stale".
    /// </summary>
    public static string Classify(SourceSyncState state, Instant now)
    {
        if (!state.IsConfigured)
        {
            return NotConfigured;
        }
        if (state.IsAvailable == false)
        {
            return Unavailable;
        }
        if (state.ConsecutiveFailureCount > 0)
        {
            return Failing;
        }
        if (state.LastSuccessAt is null)
        {
            return Configured;
        }

        var elapsedNanoseconds = (now - state.LastSuccessAt.Value).ToInt128Nanoseconds();
        var staleAfterNanoseconds =
            (Int128)state.ExpectedRefreshIntervalSeconds * 2 * NodaConstants.NanosecondsPerSecond;
        return elapsedNanoseconds > staleAfterNanoseconds ? Stale : Fresh;
    }
}
