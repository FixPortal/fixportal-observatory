using AiObservatory.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace AiObservatory.Data.Repositories;

public sealed class SourceSyncStateStore(AiObservatoryDbContext db)
{
    public Task<SourceSyncState?> GetAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        return db
            .SourceSyncStates.AsNoTracking()
            .SingleOrDefaultAsync(state => state.SourceId == sourceId, cancellationToken);
    }

    public Task MarkUnconfiguredAsync(
        string sourceId,
        Duration expectedRefreshInterval,
        Instant current,
        CancellationToken cancellationToken
    )
    {
        _ = current;
        var expectedSeconds = ExpectedSeconds(expectedRefreshInterval);
        return db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "SourceSyncStates"
                ("SourceId", "IsConfigured", "IsAvailable", "ExpectedRefreshIntervalSeconds",
                 "LastAttemptAt", "LastSuccessAt", "LatestObservationAt", "ConsecutiveFailureCount", "LastError")
            VALUES
                ({sourceId}, FALSE, NULL, {expectedSeconds}, NULL, NULL, NULL, 0, NULL)
            ON CONFLICT ("SourceId") DO UPDATE SET
                "IsConfigured" = FALSE,
                "IsAvailable" = NULL,
                "ExpectedRefreshIntervalSeconds" = EXCLUDED."ExpectedRefreshIntervalSeconds",
                "ConsecutiveFailureCount" = 0,
                "LastError" = NULL
            """,
            cancellationToken
        );
    }

    public Task MarkAttemptAsync(
        string sourceId,
        Duration expectedRefreshInterval,
        Instant current,
        CancellationToken cancellationToken,
        LocalDate? pendingFromDate = null
    )
    {
        var expectedSeconds = ExpectedSeconds(expectedRefreshInterval);
        return db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "SourceSyncStates"
                ("SourceId", "IsConfigured", "IsAvailable", "ExpectedRefreshIntervalSeconds",
                 "LastAttemptAt", "LastSuccessAt", "LatestObservationAt", "PendingFromDate",
                 "ConsecutiveFailureCount", "LastError")
            VALUES
                ({sourceId}, TRUE, NULL, {expectedSeconds}, {current}, NULL, NULL, {pendingFromDate}, 0, NULL)
            ON CONFLICT ("SourceId") DO UPDATE SET
                "IsConfigured" = TRUE,
                "ExpectedRefreshIntervalSeconds" = EXCLUDED."ExpectedRefreshIntervalSeconds",
                "LastAttemptAt" = GREATEST("SourceSyncStates"."LastAttemptAt", EXCLUDED."LastAttemptAt"),
                "PendingFromDate" = LEAST("SourceSyncStates"."PendingFromDate", EXCLUDED."PendingFromDate")
            """,
            cancellationToken
        );
    }

    public Task MarkSuccessAsync(
        string sourceId,
        Duration expectedRefreshInterval,
        Instant current,
        Instant? latestObservationAt,
        CancellationToken cancellationToken
    ) => MarkSuccessAsync(db, sourceId, expectedRefreshInterval, current, latestObservationAt, cancellationToken);

    internal static Task MarkSuccessAsync(
        AiObservatoryDbContext db,
        string sourceId,
        Duration expectedRefreshInterval,
        Instant current,
        Instant? latestObservationAt,
        CancellationToken cancellationToken
    )
    {
        var expectedSeconds = ExpectedSeconds(expectedRefreshInterval);
        return db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "SourceSyncStates"
                ("SourceId", "IsConfigured", "IsAvailable", "ExpectedRefreshIntervalSeconds",
                 "LastAttemptAt", "LastSuccessAt", "LatestObservationAt", "ConsecutiveFailureCount", "LastError")
            VALUES
                ({sourceId}, TRUE, TRUE, {expectedSeconds}, {current}, {current}, {latestObservationAt}, 0, NULL)
            ON CONFLICT ("SourceId") DO UPDATE SET
                "IsConfigured" = TRUE,
                "IsAvailable" = TRUE,
                "ExpectedRefreshIntervalSeconds" = EXCLUDED."ExpectedRefreshIntervalSeconds",
                "LastAttemptAt" = GREATEST("SourceSyncStates"."LastAttemptAt", EXCLUDED."LastAttemptAt"),
                "LastSuccessAt" = GREATEST("SourceSyncStates"."LastSuccessAt", EXCLUDED."LastSuccessAt"),
                "LatestObservationAt" = GREATEST(
                    "SourceSyncStates"."LatestObservationAt",
                    EXCLUDED."LatestObservationAt"
                ),
                "PendingFromDate" = NULL,
                "ConsecutiveFailureCount" = 0,
                "LastError" = NULL
            WHERE "SourceSyncStates"."LastAttemptAt" IS NULL
                OR EXCLUDED."LastAttemptAt" >= "SourceSyncStates"."LastAttemptAt"
            """,
            cancellationToken
        );
    }

    public Task<int> MarkUnavailableAsync(
        string sourceId,
        Duration expectedRefreshInterval,
        Instant current,
        string error,
        CancellationToken cancellationToken,
        bool onlyIfLatestAttempt = false
    ) =>
        MarkFailedAttemptAsync(
            sourceId,
            expectedRefreshInterval,
            current,
            error,
            false,
            cancellationToken,
            onlyIfLatestAttempt
        );

    public Task<int> MarkFailureAsync(
        string sourceId,
        Duration expectedRefreshInterval,
        Instant current,
        string error,
        CancellationToken cancellationToken,
        bool onlyIfLatestAttempt = false
    ) =>
        MarkFailedAttemptAsync(
            sourceId,
            expectedRefreshInterval,
            current,
            error,
            null,
            cancellationToken,
            onlyIfLatestAttempt
        );

    private async Task<int> MarkFailedAttemptAsync(
        string sourceId,
        Duration expectedRefreshInterval,
        Instant current,
        string error,
        bool? isAvailable,
        CancellationToken cancellationToken,
        bool onlyIfLatestAttempt
    )
    {
        var expectedSeconds = ExpectedSeconds(expectedRefreshInterval);
        var rowsAffected = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "SourceSyncStates"
                ("SourceId", "IsConfigured", "IsAvailable", "ExpectedRefreshIntervalSeconds",
                 "LastAttemptAt", "LastSuccessAt", "LatestObservationAt", "ConsecutiveFailureCount", "LastError")
            VALUES
                ({sourceId}, TRUE, {isAvailable}, {expectedSeconds}, {current}, NULL, NULL, 1, {error})
            ON CONFLICT ("SourceId") DO UPDATE SET
                "IsConfigured" = TRUE,
                "IsAvailable" = COALESCE(EXCLUDED."IsAvailable", "SourceSyncStates"."IsAvailable"),
                "ExpectedRefreshIntervalSeconds" = EXCLUDED."ExpectedRefreshIntervalSeconds",
                "LastAttemptAt" = GREATEST("SourceSyncStates"."LastAttemptAt", EXCLUDED."LastAttemptAt"),
                "ConsecutiveFailureCount" = "SourceSyncStates"."ConsecutiveFailureCount" + 1,
                "LastError" = EXCLUDED."LastError"
            WHERE NOT {onlyIfLatestAttempt}
                OR "SourceSyncStates"."LastAttemptAt" IS NULL
                OR EXCLUDED."LastAttemptAt" >= "SourceSyncStates"."LastAttemptAt"
            """,
            cancellationToken
        );
        if (onlyIfLatestAttempt && rowsAffected == 0)
        {
            return -1;
        }

        return await db
            .SourceSyncStates.AsNoTracking()
            .Where(state => state.SourceId == sourceId)
            .Select(state => state.ConsecutiveFailureCount)
            .SingleAsync(cancellationToken);
    }

    /// <summary>
    /// Records one sweep report (success when <paramref name="error"/> is null, unavailable
    /// otherwise) and its held-file total in a single statement, so the row can never hold one
    /// report's status with another's total. Applied only when <paramref name="attemptedAt"/> is
    /// strictly later than the stored attempt: a stalled older report changes nothing, and a retry
    /// of the same attempt changes nothing either, its failure counter included.
    /// </summary>
    /// <returns>1 when the report was applied, 0 when an equal or newer attempt was already stored.</returns>
    public Task<int> RecordSweepReportAsync(
        string sourceId,
        Duration expectedRefreshInterval,
        Instant attemptedAt,
        string? error,
        int? heldFiles,
        CancellationToken cancellationToken
    )
    {
        var expectedSeconds = ExpectedSeconds(expectedRefreshInterval);
        var success = error is null;
        Instant? successAt = success ? attemptedAt : null;
        var failures = success ? 0 : 1;
        return db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "SourceSyncStates"
                ("SourceId", "IsConfigured", "IsAvailable", "ExpectedRefreshIntervalSeconds",
                 "LastAttemptAt", "LastSuccessAt", "LatestObservationAt", "ConsecutiveFailureCount",
                 "LastError", "HeldFiles")
            VALUES
                ({sourceId}, TRUE, {success}, {expectedSeconds}, {attemptedAt}, {successAt}, NULL,
                 {failures}, {error}, {heldFiles})
            ON CONFLICT ("SourceId") DO UPDATE SET
                "IsConfigured" = TRUE,
                "IsAvailable" = EXCLUDED."IsAvailable",
                "ExpectedRefreshIntervalSeconds" = EXCLUDED."ExpectedRefreshIntervalSeconds",
                "LastAttemptAt" = EXCLUDED."LastAttemptAt",
                "LastSuccessAt" = COALESCE(EXCLUDED."LastSuccessAt", "SourceSyncStates"."LastSuccessAt"),
                "PendingFromDate" = NULL,
                "ConsecutiveFailureCount" = CASE WHEN EXCLUDED."IsAvailable"
                    THEN 0 ELSE "SourceSyncStates"."ConsecutiveFailureCount" + 1 END,
                "LastError" = EXCLUDED."LastError",
                "HeldFiles" = EXCLUDED."HeldFiles"
            WHERE "SourceSyncStates"."LastAttemptAt" IS NULL
                OR "SourceSyncStates"."LastAttemptAt" < EXCLUDED."LastAttemptAt"
            """,
            cancellationToken
        );
    }

    private static long ExpectedSeconds(Duration expectedRefreshInterval) =>
        checked((long)expectedRefreshInterval.TotalSeconds);
}
