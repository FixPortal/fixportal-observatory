using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using NodaTime;

namespace AiObservatory.Api.Services;

// Request properties are consumed by endpoint binding and service validation.
// EventType is retained as part of the external ingestion contract.
// ReSharper disable NotAccessedPositionalProperty.Global

public class AdversarialReviewService(IAdversarialReviewRepository repo, IClock clock)
{
    private static readonly Dictionary<string, string> ModelAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sonnet"] = "claude-sonnet-4-6",
        ["claude-sonnet"] = "claude-sonnet-4-6",
        ["opus"] = "claude-opus-4-8",
        ["claude-opus"] = "claude-opus-4-8",
        ["haiku"] = "claude-haiku-4-5",
        ["claude-haiku"] = "claude-haiku-4-5",
    };

    // Aliases are Anthropic short-names; only resolve them for the anthropic
    // reviewer so a non-Anthropic run posting "opus"/"sonnet"/"haiku" is not
    // silently rewritten to a claude-* model id.
    private static string NormalizeModel(string model, string reviewer)
    {
        var trimmed = model.Trim();
        if (reviewer != "anthropic")
        {
            return trimmed;
        }
        return ModelAliases.GetValueOrDefault(trimmed, trimmed);
    }

    private const int SummaryMaxLength = 80;

    private static bool IsReviewerSlug(string reviewer) =>
        reviewer.Length > 0
        && reviewer[0] is >= 'a' and <= 'z'
        && reviewer.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    // Operator-supplied run name: trim, null when blank, hard-cap at the column
    // length so an over-long name truncates rather than 500ing on insert.
    private static string? NormalizeSummary(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return null;
        }
        var trimmed = summary.Trim();
        if (trimmed.Length <= SummaryMaxLength)
        {
            return trimmed;
        }
        // Don't slice through a surrogate pair (would yield an invalid lone
        // surrogate); back off one unit if the cut would land mid-pair.
        var cut = char.IsHighSurrogate(trimmed[SummaryMaxLength - 1]) ? SummaryMaxLength - 1 : SummaryMaxLength;
        return trimmed[..cut];
    }

#pragma warning disable S3776 // Linear request guards are clearer here than fragmented validation helpers.
    public async Task<IResult> RecordRunAsync(AdversarialReviewRunRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Reviewer))
        {
            return Results.BadRequest("Reviewer is required");
        }

        var reviewer = req.Reviewer.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(req.Model))
        {
            return Results.BadRequest("Model is required");
        }

        if (string.IsNullOrWhiteSpace(req.RunId))
        {
            return Results.BadRequest("RunId is required");
        }

        // Length-guard the caller-supplied, column-capped string fields so an over-long
        // value returns 400 rather than surfacing as a Postgres 22001 / 500 on insert.
        // (Summary is truncated by NormalizeSummary; Role is enum-validated below.)
        if (reviewer.Length > 100)
        {
            return Results.BadRequest("Reviewer must be 100 characters or fewer");
        }

        // Shape-validate, don't membership-validate: the reviewer set is whichever CLIs the
        // operator runs, so a hardcoded vendor allowlist would 400 a new reviewer until a code
        // change shipped. A slug shape (starts with a letter, then lowercase letters, digits
        // or hyphens) rejects prose and punctuation without enumerating who is allowed.
        if (!IsReviewerSlug(reviewer))
        {
            return Results.BadRequest(
                "Reviewer must be a vendor slug (a lowercase letter, then lowercase letters, digits, or hyphens)"
            );
        }

        if (req.Model.Trim().Length > 200)
        {
            return Results.BadRequest("Model must be 200 characters or fewer");
        }

        if (req.RunId.Trim().Length > 200)
        {
            return Results.BadRequest("RunId must be 200 characters or fewer");
        }

        if (req.Repo is not null && req.Repo.Trim().Length > 200)
        {
            return Results.BadRequest("Repo must be 200 characters or fewer");
        }

        if (req.Role is not ("reviewer" or "judge"))
        {
            return Results.BadRequest("Role must be 'reviewer' or 'judge'");
        }

        if (req.InputTokens < 0 || req.OutputTokens < 0)
        {
            return Results.BadRequest("Token counts must be non-negative");
        }

        if (req.CostUsd < 0)
        {
            return Results.BadRequest("CostUsd must be non-negative");
        }

        if (req.IssuesRaised < 0 || req.IssuesAccepted < 0)
        {
            return Results.BadRequest("Issue counts must be non-negative");
        }

        if (req.Role == "judge" && (req.IssuesRaised != 0 || req.IssuesAccepted != 0))
        {
            return Results.BadRequest("Judge runs must have zero issue counts");
        }

        if (req.ChunkCount is <= 0)
        {
            return Results.BadRequest("ChunkCount must be positive when supplied");
        }

        decimal? costPerAcceptedFinding = req.IssuesAccepted > 0 ? req.CostUsd / req.IssuesAccepted : null;

        var run = new AdversarialReviewRun
        {
            Reviewer = reviewer,
            Model = NormalizeModel(req.Model, reviewer),
            InputTokens = req.InputTokens,
            OutputTokens = req.OutputTokens,
            CostUsd = req.CostUsd,
            ReviewDurationMs = req.ReviewDurationMs,
            IssuesRaised = req.IssuesRaised,
            IssuesAccepted = req.IssuesAccepted,
            CostPerAcceptedFinding = costPerAcceptedFinding,
            ChunkCount = req.ChunkCount,
            RunId = req.RunId.Trim(),
            Role = req.Role,
            Repo = req.Repo?.Trim(),
            Summary = NormalizeSummary(req.Summary),
            RecordedAt = clock.GetCurrentInstant(),
        };

        var (id, existed) = await repo.RecordRunAsync(run, ct);

        // An existing participant row is corrected in place (last-write-wins), not
        // rejected — report it as updated rather than a no-op duplicate.
        return existed
            ? Results.Ok(new { Id = id, Updated = true })
            : Results.Created($"/api/adversarial-review/runs/{id}", new { Id = id });
    }
#pragma warning restore S3776
}

public record AdversarialReviewRunRequest(
    string EventType,
    string Reviewer,
    string Model,
    long InputTokens,
    long OutputTokens,
    decimal CostUsd,
    long ReviewDurationMs,
    int IssuesRaised,
    int IssuesAccepted,
    string RunId,
    string Role,
    string? Repo,
    string? Summary = null,
    int? ChunkCount = null
);
