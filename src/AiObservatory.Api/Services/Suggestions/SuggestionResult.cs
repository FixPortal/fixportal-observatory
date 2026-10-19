using AiObservatory.Data.Entities;
using NodaTime;

namespace AiObservatory.Api.Services.Suggestions;

public sealed record DateWindow(LocalDate From, LocalDate To);

/// <summary>Everything the rules read, loaded once so the rules themselves are pure functions.</summary>
/// <param name="UsdToGbp">Null means no live rate is available; plan rules then report not evaluated.</param>
public sealed record SuggestionInputs(
    DateWindow SeatWindow,
    DateWindow PlanWindow,
    LocalDate Today,
    IReadOnlyList<AdversarialReviewRun> Runs,
    IReadOnlyList<AdversarialReviewFinding> Findings,
    IReadOnlyList<Subscription> Subscriptions,
    IReadOnlyList<DailyAggregate> Aggregates,
    decimal? UsdToGbp
);

/// <param name="Value">A number, or text for a descriptive figure such as the FX source.</param>
public sealed record SuggestionFigure(string Label, object Value, string Unit);

public sealed record SuggestionEvidence(
    DateWindow Window,
    int SampleSize,
    IReadOnlyList<SuggestionFigure> Figures,
    IReadOnlyList<string> RunIds
);

/// <param name="PrimaryFigureLabel">The figure the reopen rule compares against its baseline.</param>
public sealed record SuggestionCandidate(
    string RuleId,
    string Subject,
    string Severity,
    string Title,
    SuggestionEvidence Evidence,
    string PrimaryFigureLabel
);

/// <summary>One rule's result. <c>Evaluated</c> is false when no subject could be judged.</summary>
public sealed record RuleOutcome(
    string RuleId,
    IReadOnlyList<SuggestionCandidate> Candidates,
    bool Evaluated,
    string? Reason,
    int SubjectsEvaluated
)
{
    public static RuleOutcome NotEvaluated(string ruleId, string reason) => new(ruleId, [], false, reason, 0);
}

public interface ISuggestionRule
{
    string RuleId { get; }

    RuleOutcome Evaluate(SuggestionInputs inputs, SuggestionOptions options);
}
