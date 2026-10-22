using AiObservatory.Data.Audit;

namespace AiObservatory.Api.Audit;

public sealed class AuditOptions : AuditStoreOptions
{
    public const string SectionName = "Audit";
    public bool Enabled { get; set; }
    public bool WorkerEnabled { get; set; } = true;
}
