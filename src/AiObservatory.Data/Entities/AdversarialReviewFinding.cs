using NodaTime;

namespace AiObservatory.Data.Entities;

/// <summary>
/// One finding from one adversarial review run: its severity, its final outcome and who
/// took part. No finding text is stored; the run folder named by <see cref="RunId"/> holds it.
/// </summary>
public sealed class AdversarialReviewFinding
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string RunId { get; init; } = "";

    /// <summary>"single" for an unchunked run.</summary>
    public string ChunkId { get; init; } = "";

    public string FindingId { get; init; } = "";
    public string Severity { get; init; } = "unknown";
    public string Outcome { get; init; } = "";
    public Instant RecordedAt { get; init; }
    public List<AdversarialReviewFindingParticipant> Participants { get; init; } = [];
}

public sealed class AdversarialReviewFindingParticipant
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid FindingId { get; init; }
    public string Reviewer { get; init; } = "";
    public string Model { get; init; } = "";

    /// <summary>"raiser" or "assessor".</summary>
    public string Kind { get; init; } = "";

    /// <summary>Null for raisers.</summary>
    public string? Verdict { get; init; }
}
