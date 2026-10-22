using NodaTime;

namespace AiObservatory.Data.Audit;

internal sealed class AuditRun
{
    public Guid Id { get; set; }
    public string InstanceId { get; set; } = "";
    public byte[] Scope { get; set; } = [];
    public string IdempotencyKey { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string State { get; set; } = "queued";
    public string Stage { get; set; } = "queued";
    public bool PartialEvidenceAcknowledged { get; set; }
    public Guid[] CaptureRequestIds { get; set; } = [];
    public long Revision { get; set; }
    public int Attempts { get; set; }
    public Guid? WorkerId { get; set; }
    public Instant? LeaseExpiresAt { get; set; }
    public Instant CreatedAt { get; set; }
    public Instant? CompletedAt { get; set; }
    public Instant? EvidenceExpiresAt { get; set; }
    public Instant? SummaryExpiresAt { get; set; }
    public byte[]? Result { get; set; }
    public byte[]? SanitizedSummary { get; set; }
    public Instant? ServerCutoff { get; set; }
    public string? ResultHash { get; set; }
    public string? DiagnosticCode { get; set; }
    public Guid? PredecessorRunId { get; set; }
    public Guid? EvaluationOfRunId { get; set; }
}

internal sealed class AuditRunCapture
{
    public Guid RunId { get; set; }
    public Guid CaptureId { get; set; }
}
