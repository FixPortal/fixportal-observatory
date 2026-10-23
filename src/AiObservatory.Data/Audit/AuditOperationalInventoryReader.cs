using System.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace AiObservatory.Data.Audit;

public interface IAuditOperationalInventoryReader
{
    Task<AuditOperationalInventory> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>Fixed aggregate projections; never loads evidence or initializes retention policy.</summary>
public sealed class AuditOperationalInventoryReader(AiObservatoryDbContext db, string instanceId)
    : IAuditOperationalInventoryReader
{
    public async Task<AuditOperationalInventory> ReadAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
        await db.Database.ExecuteSqlRawAsync(
            "SET LOCAL statement_timeout = '3s'; SET LOCAL lock_timeout = '1s'; SET LOCAL idle_in_transaction_session_timeout = '5s'",
            ct
        );

        // SQL identifiers and DTO aliases are literals. Only the fixed instance value is parameterized.
        var observed = await db.Database.SqlQuery<Instant>($"SELECT current_timestamp AS \"Value\"").SingleAsync(ct);
        var policy =
            await db
                .Database.SqlQuery<InventoryPolicy>(
                    $"""
                    SELECT true AS "StoredRowPresent", "EvidenceDays", "SummaryDays", "Revision"
                    FROM public."AuditRetentionSettings" WHERE "InstanceId"={instanceId}
                    """
                )
                .SingleOrDefaultAsync(ct)
            ?? new InventoryPolicy(false, null, null, null);
        var runs = await db
            .Database.SqlQuery<InventoryRuns>(
                $"""
                SELECT count(*) AS "Total",
                  count(*) FILTER(WHERE "State"='queued') AS "Queued",
                  count(*) FILTER(WHERE "State"='collecting') AS "Collecting",
                  count(*) FILTER(WHERE "State"='executing') AS "Executing",
                  count(*) FILTER(WHERE "State"='completed') AS "Completed",
                  count(*) FILTER(WHERE "State"='failed') AS "Failed",
                  count(*) FILTER(WHERE "State"='cancelled') AS "Cancelled",
                  count(*) FILTER(WHERE "State" IS NULL OR "State" NOT IN ('collecting','queued','executing','completed','cancelled','failed')) AS "UnexpectedState",
                  count(*) FILTER(WHERE "State"='executing' AND "LeaseExpiresAt">current_timestamp) AS "LiveExecutionLease",
                  count(*) FILTER(WHERE "State"='executing' AND ("LeaseExpiresAt" IS NULL OR "LeaseExpiresAt"<=current_timestamp)) AS "StaleOrMissingExecutionLease",
                  count(*) FILTER(WHERE "EvidenceExpiresAt"<=current_timestamp) AS "EvidenceExpired",
                  count(*) FILTER(WHERE "SummaryExpiresAt"<=current_timestamp) AS "SummaryExpired"
                FROM public."AuditRuns" WHERE "InstanceId"={instanceId}
                """
            )
            .SingleAsync(ct);
        var exports = await db
            .Database.SqlQuery<InventoryExportLeases>(
                $"""
                SELECT count(*) AS "Total",
                  count(*) FILTER(WHERE "Cancelled") AS "Cancelled",
                  count(*) FILTER(WHERE "ExpiresAt"<=current_timestamp) AS "Expired",
                  count(*) FILTER(WHERE NOT "Cancelled" AND "ExpiresAt">current_timestamp) AS "NoncancelledUnexpired",
                  coalesce(sum("ReservedBytes") FILTER(WHERE NOT "Cancelled" AND "ExpiresAt">current_timestamp),0) AS "NoncancelledUnexpiredReservedBytes",
                  count(*) FILTER(WHERE "ExpiresAt">current_timestamp) AS "CapacityUnexpiredLeases",
                  coalesce(sum("ReservedBytes") FILTER(WHERE "ExpiresAt">current_timestamp),0) AS "CapacityUnexpiredReservedBytes"
                FROM public."AuditExportReadLeases" WHERE "InstanceId"={instanceId}
                """
            )
            .SingleAsync(ct);
        var requests = await db
            .Database.SqlQuery<InventoryCaptureRequests>(
                $"""
                SELECT count(*) AS "Total",
                  count(*) FILTER(WHERE "State"='pending') AS "Pending",
                  count(*) FILTER(WHERE "State"='uploading') AS "Uploading",
                  count(*) FILTER(WHERE "State"='sealed') AS "Sealed",
                  count(*) FILTER(WHERE "State"='expired') AS "Expired",
                  count(*) FILTER(WHERE "State" IS NULL OR "State" NOT IN ('pending','uploading','sealed','expired')) AS "UnexpectedState",
                  count(*) FILTER(WHERE "State" IN ('pending','uploading') AND "Deadline"<=current_timestamp) AS "Overdue",
                  count(*) FILTER(WHERE "State"='uploading' AND "LeaseExpiresAt">current_timestamp) AS "LiveUploadLease",
                  count(*) FILTER(WHERE "State"='uploading' AND "LeaseExpiresAt"<=current_timestamp) AS "ExpiredUploadLease",
                  count(*) FILTER(WHERE "State"='uploading' AND "LeaseExpiresAt" IS NULL) AS "MissingUploadLease"
                FROM public."AuditCaptureRequests"
                """
            )
            .SingleAsync(ct);
        var captures = await db
            .Database.SqlQuery<InventoryCaptures>(
                $"""
                SELECT count(*) AS "Total",
                  count(*) FILTER(WHERE "DeletedAt" IS NULL AND ("ExpiresAt" IS NULL OR "ExpiresAt">current_timestamp)) AS "Available",
                  count(*) FILTER(WHERE "DeletedAt" IS NOT NULL) AS "MarkedDeleted",
                  count(*) FILTER(WHERE "ExpiresAt"<=current_timestamp) AS "Expired"
                FROM public."AuditEvidenceCaptures"
                """
            )
            .SingleAsync(ct);
        var credentials = await db
            .Database.SqlQuery<InventoryCredentials>(
                $"""
                SELECT count(*) AS "Total",
                  count(*) FILTER(WHERE "RevokedAt" IS NULL) AS "Nonrevoked",
                  count(*) FILTER(WHERE "RevokedAt" IS NOT NULL) AS "Revoked",
                  count(DISTINCT "MachineId") AS "RepresentedMachines",
                  min("CreatedAt") AS "OldestCreatedAt", max("CreatedAt") AS "NewestCreatedAt"
                FROM public."AuditMachineCredentials"
                """
            )
            .SingleAsync(ct);
        var machines = await db
            .Database.SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM public.\"AuditMachines\"")
            .SingleAsync(ct);
        await transaction.RollbackAsync(ct);
        return new(1, observed, new(true, true), policy, runs, exports, requests, captures, credentials, machines);
    }
}
