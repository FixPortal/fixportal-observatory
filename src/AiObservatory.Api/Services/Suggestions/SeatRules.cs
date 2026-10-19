using System.Globalization;

namespace AiObservatory.Api.Services.Suggestions;

/// <summary>
/// Shared plumbing for the seat rules. Every seat rule is stated in detailed runs (runs that have
/// finding rows) and finding-row catches only, so counts-only history can never satisfy a floor.
/// </summary>
public abstract class SeatRuleBase : ISuggestionRule
{
    private const int MaxRunIds = 20;

    public abstract string RuleId { get; }

    protected virtual string Severity => "attention";

    public abstract RuleOutcome Evaluate(SuggestionInputs inputs, SuggestionOptions options);

    protected static int DetailedRuns(SeatMetrics seat) => seat.Runs - seat.CountsOnlyRuns;

    protected static IReadOnlyList<SeatMetrics> Metrics(SuggestionInputs inputs) =>
        SeatMetricsCalculator.Compute(inputs.Runs, inputs.Findings);

    protected static string Fixed(double value, string format = "0.##") =>
        value.ToString(format, CultureInfo.InvariantCulture);

    protected SuggestionCandidate Candidate(
        SuggestionInputs inputs,
        SeatMetrics seat,
        string title,
        string primaryFigureLabel,
        params SuggestionFigure[] figures
    )
    {
        var runIds = inputs
            .Runs.Where(r => r.Role == "reviewer" && r.Model == seat.Key)
            .OrderByDescending(r => r.RecordedAt)
            .Select(r => r.RunId)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxRunIds)
            .ToList();
        return new SuggestionCandidate(
            RuleId,
            seat.Key,
            Severity,
            title,
            new SuggestionEvidence(inputs.SeatWindow, DetailedRuns(seat), figures, runIds),
            primaryFigureLabel
        );
    }

    protected RuleOutcome Outcome(
        IReadOnlyList<SuggestionCandidate> candidates,
        int subjectsEvaluated,
        string notEvaluatedReason
    ) =>
        subjectsEvaluated == 0
            ? RuleOutcome.NotEvaluated(RuleId, notEvaluatedReason)
            : new RuleOutcome(RuleId, candidates, true, null, subjectsEvaluated);
}

public sealed class SeatNoUniqueCatchRule : SeatRuleBase
{
    public override string RuleId => "seat.no-unique-catch";

    public override RuleOutcome Evaluate(SuggestionInputs inputs, SuggestionOptions options)
    {
        var judged = Metrics(inputs).Where(s => DetailedRuns(s) >= options.MinDetailedRuns).ToList();
        var candidates = judged
            .Where(s => s.Unique == 0)
            .Select(s =>
                Candidate(
                    inputs,
                    s,
                    $"Seat {s.Label} added no unique accepted catch in {DetailedRuns(s)} runs",
                    "Detailed runs",
                    new SuggestionFigure("Detailed runs", DetailedRuns(s), "runs"),
                    new SuggestionFigure("Unique accepted catches", s.Unique, "findings"),
                    new SuggestionFigure("Corroborated accepted catches", s.Corroborated, "findings"),
                    new SuggestionFigure("Known cost", s.KnownCostUsd, "USD")
                )
            )
            .ToList();
        return Outcome(
            candidates,
            judged.Count,
            $"No seat has {options.MinDetailedRuns} runs with finding rows in the last {options.SeatWindowDays} days yet"
        );
    }
}

public sealed class SeatHighFalsePositiveRule : SeatRuleBase
{
    public override string RuleId => "seat.high-false-positive";

    public override RuleOutcome Evaluate(SuggestionInputs inputs, SuggestionOptions options)
    {
        var judged = Metrics(inputs)
            .Where(s => s.Unique + s.Corroborated + s.Refuted >= options.MinSettledFindings)
            .ToList();
        var candidates = judged
            .Where(s => s.FalsePositiveRate > options.FalsePositiveThreshold)
            .Select(s =>
                Candidate(
                    inputs,
                    s,
                    $"Seat {s.Label} has a {Fixed(s.FalsePositiveRate!.Value * 100, "0")}% false-positive rate over {s.Unique + s.Corroborated + s.Refuted} settled findings",
                    "False-positive rate",
                    new SuggestionFigure("False-positive rate", Math.Round(s.FalsePositiveRate.Value, 4), "ratio"),
                    new SuggestionFigure("Refuted findings", s.Refuted, "findings"),
                    new SuggestionFigure("Accepted findings", s.Unique + s.Corroborated, "findings")
                )
            )
            .ToList();
        return Outcome(
            candidates,
            judged.Count,
            $"No seat has {options.MinSettledFindings} settled findings in the last {options.SeatWindowDays} days yet"
        );
    }
}

public sealed class SeatConfirmationOnlyRule : SeatRuleBase
{
    public override string RuleId => "seat.confirmation-only";

    protected override string Severity => "info";

    public override RuleOutcome Evaluate(SuggestionInputs inputs, SuggestionOptions options)
    {
        var judged = Metrics(inputs)
            .Where(s => s.Unique + s.Corroborated >= options.MinCatchesForConfirmationOnly)
            .ToList();
        var candidates = judged
            .Where(s => s.Unique == 0)
            .Select(s =>
                Candidate(
                    inputs,
                    s,
                    $"Seat {s.Label}'s accepted catches were all also raised by another seat ({s.Corroborated} of {s.Corroborated})",
                    "Corroborated accepted catches",
                    new SuggestionFigure("Corroborated accepted catches", s.Corroborated, "findings"),
                    new SuggestionFigure("Unique accepted catches", s.Unique, "findings")
                )
            )
            .ToList();
        return Outcome(
            candidates,
            judged.Count,
            $"No seat has {options.MinCatchesForConfirmationOnly} accepted catches in the last {options.SeatWindowDays} days yet"
        );
    }
}

public sealed class SeatCostOutlierRule : SeatRuleBase
{
    public override string RuleId => "seat.cost-outlier";

    public override RuleOutcome Evaluate(SuggestionInputs inputs, SuggestionOptions options)
    {
        // A seat takes part only with enough detailed runs AND a cost per unique catch; a seat
        // without one is neither judged nor used as a peer.
        var eligible = Metrics(inputs)
            .Where(s => DetailedRuns(s) >= options.CostOutlierMinPeerRuns && s.CostPerUnique is not null)
            .ToList();
        var candidates = new List<SuggestionCandidate>();
        var judged = 0;
        foreach (var seat in eligible)
        {
            var peers = eligible.Where(p => p.Key != seat.Key).Select(p => p.CostPerUnique!.Value).Order().ToList();
            if (peers.Count < options.CostOutlierMinPeers)
            {
                continue;
            }
            judged++;
            var median = Median(peers);
            if (median > 0 && seat.CostPerUnique > options.CostOutlierMultiple * median)
            {
                var ratio = (double)(seat.CostPerUnique.Value / median);
                candidates.Add(
                    Candidate(
                        inputs,
                        seat,
                        $"Seat {seat.Label} costs ${Fixed((double)seat.CostPerUnique.Value)} per unique catch, {Fixed(ratio, "0.#")}x its peers' median",
                        "Cost per unique catch",
                        new SuggestionFigure("Cost per unique catch", Math.Round(seat.CostPerUnique.Value, 4), "USD"),
                        new SuggestionFigure("Peer median cost per unique catch", Math.Round(median, 4), "USD"),
                        new SuggestionFigure("Peers compared", peers.Count, "seats")
                    )
                );
            }
        }
        return Outcome(
            candidates,
            judged,
            $"Fewer than {options.CostOutlierMinPeers} peer seats have a cost per unique catch in the last {options.SeatWindowDays} days"
        );
    }

    private static decimal Median(IReadOnlyList<decimal> sorted) =>
        sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2m;
}
