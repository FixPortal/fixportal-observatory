using System.Text.RegularExpressions;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using NodaTime;

namespace AiObservatory.Api.Services;

public sealed partial class AdversarialReviewFindingService(IAdversarialReviewRepository repo, IClock clock)
{
    private const int MaxFindings = 2000;
    private const int MaxParticipants = 12;

    private static readonly HashSet<string> Severities = ["critical", "high", "medium", "low", "unknown"];
    private static readonly HashSet<string> Outcomes = ["accepted", "refuted", "dropped", "indeterminate"];
    private static readonly HashSet<string> Verdicts =
    [
        "agree",
        "false-positive",
        "needs-evidence",
        "needs-repo",
        "abstained",
    ];

    // \z, not $: in .NET $ also matches before a final newline, so "F1\n" would pass.
    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,40}\z")]
    private static partial Regex IdPattern();

    // The run endpoint trims and lowercases a reviewer before its slug check; do the same here so a
    // reviewer that was accepted on a run row is accepted on its findings.
    private static string NormalizeReviewer(string reviewer) => reviewer.Trim().ToLowerInvariant();

    private static bool ValidParticipant(string? reviewer, string? model) =>
        reviewer is not null
        && AdversarialReviewService.IsReviewerSlug(NormalizeReviewer(reviewer))
        && NormalizeReviewer(reviewer).Length <= 100
        && !string.IsNullOrWhiteSpace(model)
        && model.Trim().Length <= 200;

    // Shape check only. Messages are fixed strings: a rejected body is never echoed back.
    private static string? Validate(AdversarialReviewFindingsRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.RunId) || req.RunId.Trim().Length > 200)
        {
            return "RunId is required and must be 200 characters or fewer";
        }
        if (req.Repo is not null && req.Repo.Trim().Length > 200)
        {
            return "Repo must be 200 characters or fewer";
        }
        if (req.Findings is null || req.Findings.Count > MaxFindings)
        {
            return $"Findings is required and must hold at most {MaxFindings} entries";
        }

        var seen = new HashSet<(string, string)>();
        foreach (var finding in req.Findings)
        {
            var problem = ValidateFinding(finding);
            if (problem is not null)
            {
                return problem;
            }
            if (!seen.Add((finding!.ChunkId!, finding.FindingId!)))
            {
                return "Duplicate chunkId and findingId in one body";
            }
        }
        return null;
    }

    private static string? ValidateFinding(FindingDto? finding)
    {
        if (finding is null)
        {
            return "Findings must not contain null entries";
        }
        if (
            finding.ChunkId is null
            || finding.FindingId is null
            || !IdPattern().IsMatch(finding.ChunkId)
            || !IdPattern().IsMatch(finding.FindingId)
        )
        {
            return "chunkId and findingId must match [A-Za-z0-9._-] and be 1 to 40 characters";
        }
        if (finding.Severity is null || !Severities.Contains(finding.Severity))
        {
            return "Unknown severity";
        }
        if (finding.Outcome is null || !Outcomes.Contains(finding.Outcome))
        {
            return "Unknown outcome";
        }

        var assessments = finding.Assessments ?? [];
        if (finding.RaisedBy is null || finding.RaisedBy.Count == 0)
        {
            return "raisedBy must hold at least one entry";
        }
        if (finding.RaisedBy.Count + assessments.Count > MaxParticipants)
        {
            return $"A finding holds at most {MaxParticipants} participants";
        }
        if (finding.RaisedBy.Any(r => r is null || !ValidParticipant(r.Reviewer, r.Model)))
        {
            return "raisedBy entries need a reviewer slug and a model of 200 characters or fewer";
        }
        // Two entries for one seat would read as two independent raisers and turn a unique catch
        // into a corroborated one. A vendor with two different models is two seats and stays valid.
        if (
            finding.RaisedBy.Select(r => (NormalizeReviewer(r!.Reviewer!), r.Model!.Trim())).Distinct().Count()
            != finding.RaisedBy.Count
        )
        {
            return "raisedBy must not repeat a reviewer and model";
        }
        return ValidateAssessments(assessments);
    }

    private static string? ValidateAssessments(IReadOnlyList<FindingAssessmentDto?> assessments)
    {
        if (
            assessments.Any(a =>
                a is null
                || !ValidParticipant(a.Reviewer, a.Model)
                || a.Verdict is null
                || !Verdicts.Contains(a.Verdict)
            )
        )
        {
            return "assessments entries need a reviewer slug, a model and a known verdict";
        }
        // A repeated assessor would add one peer agreement per row in SeatMetricsCalculator.
        if (
            assessments.Select(a => (NormalizeReviewer(a!.Reviewer!), a.Model!.Trim())).Distinct().Count()
            != assessments.Count
        )
        {
            return "assessments must not repeat a reviewer and model";
        }
        return null;
    }

    public async Task<IResult> RecordFindingsAsync(AdversarialReviewFindingsRequest req, CancellationToken ct)
    {
        var problem = Validate(req);
        if (problem is not null)
        {
            return Results.BadRequest(problem);
        }

        var runId = req.RunId!.Trim();
        var runs = await repo.GetRunsAsync(runId, ct);
        if (runs.Count == 0)
        {
            return Results.NotFound();
        }
        // Every run row that names a repo must agree with the submitted one; rows for different
        // reviewers are posted separately, so a disagreement among them is a real mismatch.
        if (req.Repo is not null && runs.Any(r => r.Repo is not null && r.Repo != req.Repo.Trim()))
        {
            return Results.BadRequest("Repo does not match the run");
        }

        var now = clock.GetCurrentInstant();
        var rows = req.Findings!.Select(f => ToEntity(runId, f!, now)).ToList();
        return await repo.ReplaceFindingsAsync(runId, rows, ct)
            ? Results.Ok(new { Findings = rows.Count })
            : Results.NotFound();
    }

    private static AdversarialReviewFinding ToEntity(string runId, FindingDto f, Instant now)
    {
        var id = Guid.NewGuid();
        var raisers = f.RaisedBy!.Select(r => new AdversarialReviewFindingParticipant
        {
            FindingId = id,
            Reviewer = r!.Reviewer!.Trim().ToLowerInvariant(),
            Model = r.Model!.Trim(),
            Kind = "raiser",
        });
        var assessors = (f.Assessments ?? []).Select(a => new AdversarialReviewFindingParticipant
        {
            FindingId = id,
            Reviewer = a!.Reviewer!.Trim().ToLowerInvariant(),
            Model = a.Model!.Trim(),
            Kind = "assessor",
            Verdict = a.Verdict,
        });
        return new AdversarialReviewFinding
        {
            Id = id,
            RunId = runId,
            ChunkId = f.ChunkId!,
            FindingId = f.FindingId!,
            Severity = f.Severity!,
            Outcome = f.Outcome!,
            RecordedAt = now,
            Participants = [.. raisers, .. assessors],
        };
    }
}

public record FindingParticipantDto(string? Reviewer, string? Model);

public record FindingAssessmentDto(string? Reviewer, string? Model, string? Verdict);

public record FindingDto(
    string? ChunkId,
    string? FindingId,
    string? Severity,
    string? Outcome,
    IReadOnlyList<FindingParticipantDto?>? RaisedBy,
    IReadOnlyList<FindingAssessmentDto?>? Assessments
);

public record AdversarialReviewFindingsRequest(string? RunId, string? Repo, IReadOnlyList<FindingDto?>? Findings);
