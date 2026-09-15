using System.Globalization;
using System.Text;
using AiObservatory.Data.Entities;
using NodaTime;

namespace AiObservatory.Api.Services;

/// <summary>
/// The rendered digest. <see cref="DegradedCount"/> is the count that JUSTIFIED sending —
/// stale sources are described in the body but never counted here, so the subject line can
/// never claim an outage that consists entirely of a laptop that has not run its sweep.
/// </summary>
public sealed record SourceHealthDigestContent(string Subject, string Body, int DegradedCount);

/// <summary>
/// Composes the daily source-health digest. Pure: no database, no clock of its own, no
/// delivery — it takes the states and the instant and returns text, which is what makes the
/// interesting decisions (what counts as degraded, what order to list them in) testable
/// without a Postgres container.
/// </summary>
public static class SourceHealthDigest
{
    /// <summary>
    /// Returns null when nothing warrants a message, which the caller treats as "send
    /// nothing and do not consume today's claim".
    /// <para>
    /// Only <c>unavailable</c> and <c>failing</c> trigger a send. <c>stale</c> does not:
    /// the local producers (copilot-local, antigravity-local, gemini-review-local) go stale
    /// whenever the machine-side sweep has not run, which is routine and not a fault, so
    /// letting it trigger would make the digest mostly noise and train the reader to skip
    /// it. Stale sources are still LISTED when something else already justified the message,
    /// because at that point the extra context is free.
    /// </para>
    /// </summary>
    public static SourceHealthDigestContent? Compose(IEnumerable<SourceSyncState> states, Instant now)
    {
        ArgumentNullException.ThrowIfNull(states);

        var classified = states
            .Select(state => (State: state, Status: SourceHealthClassifier.Classify(state, now)))
            .ToList();

        var degraded = classified
            .Where(entry => entry.Status is SourceHealthClassifier.Unavailable or SourceHealthClassifier.Failing)
            // Unavailable outranks failing, then the longest-running breakage first: the
            // reader should meet the worst thing before deciding whether to stop reading.
            .OrderBy(entry => entry.Status == SourceHealthClassifier.Unavailable ? 0 : 1)
            .ThenByDescending(entry => entry.State.ConsecutiveFailureCount)
            .ThenBy(entry => entry.State.SourceId, StringComparer.Ordinal)
            .ToList();

        if (degraded.Count == 0)
        {
            return null;
        }

        var stale = classified
            .Where(entry => entry.Status == SourceHealthClassifier.Stale)
            .OrderBy(entry => entry.State.SourceId, StringComparer.Ordinal)
            .ToList();

        var subject = string.Create(
            CultureInfo.InvariantCulture,
            $"Observatory: {degraded.Count} ingest source{(degraded.Count == 1 ? "" : "s")} degraded"
        );

        var body = new StringBuilder();
        body.Append(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{degraded.Count} of {classified.Count} ingest sources are not healthy as of {now:uuuu-MM-dd HH:mm} UTC.\n\n"
            )
        );

        foreach (var (state, status) in degraded)
        {
            body.Append(string.Create(CultureInfo.InvariantCulture, $"{state.SourceId} — {status}\n"));
            body.Append(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"  consecutive failures: {state.ConsecutiveFailureCount}\n"
                )
            );
            body.Append(
                string.Create(CultureInfo.InvariantCulture, $"  last success: {Describe(state.LastSuccessAt)}\n")
            );
            if (!string.IsNullOrWhiteSpace(state.LastError))
            {
                body.Append(string.Create(CultureInfo.InvariantCulture, $"  last error: {state.LastError}\n"));
            }
            body.Append('\n');
        }

        if (stale.Count > 0)
        {
            body.Append("Also stale (not counted above; routine for local producers that have not swept):\n");
            foreach (var (state, _) in stale)
            {
                body.Append(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"  {state.SourceId} — last success {Describe(state.LastSuccessAt)}\n"
                    )
                );
            }
            body.Append('\n');
        }

        body.Append("Full detail: /api/sources/status\n");

        return new SourceHealthDigestContent(subject, body.ToString(), degraded.Count);
    }

    private static string Describe(Instant? instant) =>
        instant is { } value ? value.ToString("uuuu-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) : "never";
}
