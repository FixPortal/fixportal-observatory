namespace AiObservatory.Data.Audit;

internal sealed class AuditRetentionSettings
{
    public string InstanceId { get; set; } = "";
    public int? EvidenceDays { get; set; }
    public int? SummaryDays { get; set; }
    public long Revision { get; set; }
}
