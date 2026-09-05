using NodaTime;

namespace AiObservatory.Data.Entities;

/// <summary>
/// One submitted review on a pull request, keyed by GitHub's own review id.
/// </summary>
/// <remarks>
/// The reviewer is stored per review rather than folded into
/// <see cref="GitHubPullRequest.ReviewCount" /> because the question this answers is which
/// agent reviewed — CodeRabbit, Gitar, or a human — and an aggregate count cannot be
/// decomposed after the fact. GitHub's own <c>user.type</c> supplies
/// <see cref="IsBot" />; it is not inferred from the login suffix.
/// </remarks>
public sealed class GitHubPullRequestReview
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Repo { get; init; } = "";
    public int Number { get; init; }
    public long ReviewId { get; init; }
    public string Reviewer { get; init; } = "";
    public bool IsBot { get; init; }

    /// <summary>APPROVED / CHANGES_REQUESTED / COMMENTED / DISMISSED / PENDING.</summary>
    public string State { get; init; } = "";

    /// <summary>Null for a pending review that its author has not submitted yet.</summary>
    public Instant? SubmittedAt { get; init; }

    public Instant IngestedAt { get; init; }
}
