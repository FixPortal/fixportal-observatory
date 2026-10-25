using AiObservatory.Api.Services.Suggestions;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using NodaTime;

namespace AiObservatory.Api;

/// <summary>Invented complete panels with finding-level evidence for the public demo.</summary>
internal static class DemoReviewSeeder
{
    private static readonly (string Reviewer, string Model, decimal CostUsd)[] Reviewers =
    [
        ("openai", "gpt-6-luna", 0.42m),
        ("anthropic", "claude-haiku-4-5", 0.31m),
        ("google", "gemini-2.5-pro", 0.24m),
        ("zai", "glm-5", 0.12m),
        ("deepseek", "deepseek-review", 0.08m),
    ];

    private static readonly (
        string Summary,
        string Shared,
        string Google,
        string Zai,
        string Deepseek,
        string Anthropic
    )[] Scenarios =
    [
        (
            "Synthetic rate-limit review",
            "retry-after-ignored",
            "quota-reset-utc-edge",
            "burst-counter-race",
            "header-leak-claim",
            "backoff-cap-missing"
        ),
        (
            "Synthetic billing-boundary review",
            "invoice-window-overlap",
            "rounding-drift-claim",
            "billing-idempotency-gap",
            "tax-code-claim",
            "proration-boundary"
        ),
        (
            "Synthetic retry-policy review",
            "retry-budget-unbounded",
            "dead-letter-loop",
            "jitter-gap-claim",
            "timeout-leak-claim",
            "circuit-breaker-gap"
        ),
        (
            "Synthetic authorization review",
            "tenant-scope-bypass",
            "token-reuse-claim",
            "audit-log-leak",
            "role-cache-claim",
            "permission-cache-gap"
        ),
        (
            "Synthetic migration review",
            "rollback-step-missing",
            "index-lock-window",
            "dual-write-claim",
            "data-loss-claim",
            "schema-compatibility-gap"
        ),
    ];

    public static void Seed(AiObservatoryDbContext db, LocalDate today, Instant now)
    {
        var runs = new List<AdversarialReviewRun>();
        var findings = new List<AdversarialReviewFinding>();

        for (var index = 0; index < Scenarios.Length; index++)
        {
            var scenario = Scenarios[index];
            var runId = $"demo-review-{index + 1}";
            var recordedAt = now.Minus(Duration.FromDays(1 + index * 3));
            var runFindings = CreateFindings(index, runId, recordedAt);
            findings.AddRange(runFindings);

            foreach (var (reviewer, model, baseCost) in Reviewers)
            {
                var raised = runFindings.Count(f => RaisedBy(f, reviewer));
                var accepted = runFindings.Count(f => f.Outcome == "accepted" && RaisedBy(f, reviewer));
                var costUnknown = reviewer == "deepseek" && index == 2;
                var cost = costUnknown ? 0m : baseCost + index * 0.02m;
                runs.Add(
                    new AdversarialReviewRun
                    {
                        RunId = runId,
                        Reviewer = reviewer,
                        Model = model,
                        Role = "reviewer",
                        Repo = "demo/sample-service",
                        Summary = scenario.Summary,
                        RecordedAt = recordedAt,
                        InputTokens = 18000 + index * 850,
                        OutputTokens = 2400 + index * 150,
                        CostUsd = cost,
                        CostUnknown = costUnknown,
                        CostPerAcceptedFinding = !costUnknown && accepted > 0 ? cost / accepted : null,
                        ReviewDurationMs = 38000 + index * 2900,
                        IssuesRaised = raised,
                        IssuesAccepted = accepted,
                    }
                );
            }

            runs.Add(
                new AdversarialReviewRun
                {
                    RunId = runId,
                    Reviewer = "anthropic",
                    Model = "claude-opus-judge",
                    Role = "judge",
                    Repo = "demo/sample-service",
                    Summary = scenario.Summary,
                    RecordedAt = recordedAt,
                    InputTokens = 7200 + index * 250,
                    OutputTokens = 1500 + index * 70,
                    CostUsd = 0.62m + index * 0.03m,
                    ReviewDurationMs = 29000 + index * 1100,
                }
            );
        }

        db.AdversarialReviewRuns.AddRange(runs);
        db.AdversarialReviewFindings.AddRange(findings);

        // Apply a real rule to the invented evidence, so the suggestion agrees with Seats.
        var window = new DateWindow(today.PlusDays(-60), today);
        var inputs = new SuggestionInputs(window, window, today, runs, findings, [], [], null);
        var outcome = new SeatNoUniqueCatchRule().Evaluate(inputs, new SuggestionOptions());
        db.Suggestions.AddRange(outcome.Candidates.Select(candidate => SuggestionLifecycle.NewRow(candidate, now)));
    }

    private static bool RaisedBy(AdversarialReviewFinding finding, string reviewer) =>
        finding.Participants.Any(p => p.Kind == "raiser" && p.Reviewer == reviewer);

    private static List<AdversarialReviewFinding> CreateFindings(int index, string runId, Instant recordedAt)
    {
        var scenario = Scenarios[index];
        var findings = new List<AdversarialReviewFinding>
        {
            Finding(runId, scenario.Shared, "high", "accepted", recordedAt, "openai", "anthropic"),
            Finding(runId, scenario.Deepseek, "low", "refuted", recordedAt, "deepseek"),
        };
        if (index % 2 == 0)
        {
            findings.Add(Finding(runId, scenario.Google, "medium", "accepted", recordedAt, "google"));
            findings.Add(Finding(runId, scenario.Anthropic, "high", "accepted", recordedAt, "anthropic"));
        }
        else
        {
            findings.Add(Finding(runId, scenario.Zai, "medium", "accepted", recordedAt, "zai"));
        }
        if (index == 1)
        {
            findings.Add(Finding(runId, "duplicate-error-path", "low", "dropped", recordedAt, "openai"));
        }
        if (index == 3)
        {
            findings.Add(Finding(runId, "policy-precedence-unclear", "medium", "indeterminate", recordedAt, "openai"));
        }
        return findings;
    }

    private static AdversarialReviewFinding Finding(
        string runId,
        string findingId,
        string severity,
        string outcome,
        Instant recordedAt,
        params string[] raisers
    )
    {
        var id = Guid.NewGuid();
        var participants = raisers
            .Select(reviewer => new AdversarialReviewFindingParticipant
            {
                FindingId = id,
                Reviewer = reviewer,
                Model = Reviewers.First(seat => seat.Reviewer == reviewer).Model,
                Kind = "raiser",
            })
            .ToList();
        participants.Add(
            new AdversarialReviewFindingParticipant
            {
                FindingId = id,
                Reviewer = "anthropic",
                Model = "claude-opus-judge",
                Kind = "assessor",
                Verdict = outcome == "accepted" ? "agree" : "disagree",
            }
        );
        return new AdversarialReviewFinding
        {
            Id = id,
            RunId = runId,
            ChunkId = "single",
            FindingId = findingId,
            Severity = severity,
            Outcome = outcome,
            RecordedAt = recordedAt,
            Participants = participants,
        };
    }
}
