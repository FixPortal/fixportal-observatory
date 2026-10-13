using Microsoft.EntityFrameworkCore;

namespace AiObservatory.Data.Tests;

internal static class PostgresLockWait
{
    /// <summary>
    /// Waits until another session is in a PostgreSQL heavyweight-lock wait while executing a
    /// statement whose text contains <paramref name="statementFragment"/>: the deterministic
    /// replacement for "delay N ms and assert the task had not finished". The fragment must be
    /// a literal (no LIKE wildcards, no apostrophes). The poll must run outside any open
    /// transaction of its own, because pg_stat_activity is snapshotted per transaction.
    /// </summary>
    internal static async Task WaitForBlockedStatementAsync(
        AiObservatoryDbContext monitor,
        string statementFragment,
        CancellationToken ct
    )
    {
        // 600 x 50 ms = 30 s, the budget the contention helpers in this suite share.
        for (var attempt = 0; attempt < 600; attempt++)
        {
            var blocked = await monitor
                .Database.SqlQuery<bool>(
                    $"""
                    SELECT EXISTS (
                        SELECT 1
                        FROM pg_stat_activity
                        WHERE datname = current_database()
                            AND pid <> pg_backend_pid()
                            AND wait_event_type = 'Lock'
                            AND query LIKE '%' || {statementFragment} || '%'
                    ) AS "Value"
                    """
                )
                .SingleAsync(ct);
            if (blocked)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
        }

        throw new TimeoutException($"No session reached the expected PostgreSQL lock wait for '{statementFragment}'.");
    }
}
