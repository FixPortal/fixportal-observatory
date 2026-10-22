namespace AiObservatory.Data.Audit;

/// <summary>Hard protocol ceilings may be lowered by an installation, never silently raised.</summary>
public class AuditStoreOptions
{
    public int MaxChunkBytes { get; set; } = 8 * 1024 * 1024;
    public long MaxCaptureBytes { get; set; } = 512L * 1024 * 1024;
    public long MaxSnapshotBytes { get; set; } = 512L * 1024 * 1024;
    public long MaxRecordsPerCapture { get; set; } = 1_000_000;
    public int MaxRunWindowDays { get; set; } = 31;
    public int MaxExecutingRuns { get; set; } = 1;
    public int MaxOutstandingRequestsPerMachine { get; set; } = 2;
    public int MaxQueuedRuns { get; set; } = 20;
    public int MaxExecutionAttempts { get; set; } = 3;
    public TimeSpan CaptureRequestDeadline { get; set; } = TimeSpan.FromHours(24);
    public TimeSpan RunLeaseDuration { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan RunLeaseRenewalInterval { get; set; } = TimeSpan.FromMinutes(1);
    public int? EvidenceRetentionDays { get; set; } = 90;
    public int? SummaryRetentionDays { get; set; } = 365;

    public IReadOnlyList<string> ValidationErrors()
    {
        var errors = new List<string>();
        Bound(MaxChunkBytes, 8 * 1024 * 1024, nameof(MaxChunkBytes));
        Bound(MaxCaptureBytes, 512L * 1024 * 1024, nameof(MaxCaptureBytes));
        Bound(MaxSnapshotBytes, 512L * 1024 * 1024, nameof(MaxSnapshotBytes));
        Bound(MaxRecordsPerCapture, 1_000_000, nameof(MaxRecordsPerCapture));
        Bound(MaxRunWindowDays, 31, nameof(MaxRunWindowDays));
        Bound(MaxExecutingRuns, 1, nameof(MaxExecutingRuns));
        Bound(MaxOutstandingRequestsPerMachine, 2, nameof(MaxOutstandingRequestsPerMachine));
        Bound(MaxQueuedRuns, 20, nameof(MaxQueuedRuns));
        Bound(MaxExecutionAttempts, 3, nameof(MaxExecutionAttempts));
        if (MaxChunkBytes > MaxCaptureBytes)
        {
            errors.Add("MaxChunkBytes exceeds MaxCaptureBytes.");
        }
        if (CaptureRequestDeadline <= TimeSpan.Zero || CaptureRequestDeadline > TimeSpan.FromHours(24))
        {
            errors.Add("CaptureRequestDeadline must be positive and at most 24 hours.");
        }
        if (RunLeaseDuration <= TimeSpan.Zero || RunLeaseDuration > TimeSpan.FromMinutes(5))
        {
            errors.Add("RunLeaseDuration must be positive and at most 5 minutes.");
        }
        if (RunLeaseRenewalInterval <= TimeSpan.Zero || RunLeaseRenewalInterval >= RunLeaseDuration)
        {
            errors.Add("RunLeaseRenewalInterval must be positive and shorter than the lease.");
        }
        if (EvidenceRetentionDays is <= 0 || SummaryRetentionDays is <= 0)
        {
            errors.Add("Retention must be positive or explicitly null for indefinite.");
        }
        return errors;
        void Bound(long value, long ceiling, string name)
        {
            if (value <= 0 || value > ceiling)
            {
                errors.Add($"{name} must be positive and no greater than {ceiling}.");
            }
        }
    }
}
