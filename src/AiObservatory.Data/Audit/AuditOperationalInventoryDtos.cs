using NodaTime;

namespace AiObservatory.Data.Audit;

public sealed record AuditOperationalInventory(
    int SchemaVersion,
    Instant ObservedAtUtc,
    InventoryScope Scope,
    InventoryPolicy Policy,
    InventoryRuns Runs,
    InventoryExportLeases ExportLeases,
    InventoryCaptureRequests CaptureRequests,
    InventoryCaptures Captures,
    InventoryCredentials Credentials,
    long MachineCount
);

public sealed record InventoryScope(bool InstanceScopedSections, bool DatabaseGlobalSections);

public sealed record InventoryPolicy(bool StoredRowPresent, int? EvidenceDays, int? SummaryDays, long? Revision);

public sealed record InventoryRuns(
    long Total,
    long Queued,
    long Collecting,
    long Executing,
    long Completed,
    long Failed,
    long Cancelled,
    long UnexpectedState,
    long LiveExecutionLease,
    long StaleOrMissingExecutionLease,
    long EvidenceExpired,
    long SummaryExpired
);

public sealed record InventoryExportLeases(
    long Total,
    long Cancelled,
    long Expired,
    long NoncancelledUnexpired,
    decimal NoncancelledUnexpiredReservedBytes,
    long CapacityUnexpiredLeases,
    decimal CapacityUnexpiredReservedBytes
);

public sealed record InventoryCaptureRequests(
    long Total,
    long Pending,
    long Uploading,
    long Sealed,
    long Expired,
    long UnexpectedState,
    long Overdue,
    long LiveUploadLease,
    long ExpiredUploadLease,
    long MissingUploadLease
);

public sealed record InventoryCaptures(long Total, long Available, long MarkedDeleted, long Expired);

public sealed record InventoryCredentials(
    long Total,
    long Nonrevoked,
    long Revoked,
    long RepresentedMachines,
    Instant? OldestCreatedAt,
    Instant? NewestCreatedAt
);
