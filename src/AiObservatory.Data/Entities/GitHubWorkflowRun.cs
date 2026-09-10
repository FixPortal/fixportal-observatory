using NodaTime;

namespace AiObservatory.Data.Entities;

public sealed class GitHubWorkflowRun
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Repo { get; init; } = "";
    public long RunId { get; init; }
    public string WorkflowName { get; init; } = "";
    public string Status { get; init; } = ""; // success / failure / cancelled / in_progress / queued
    public Instant CreatedAt { get; init; }
    public Instant IngestedAt { get; init; }
}
