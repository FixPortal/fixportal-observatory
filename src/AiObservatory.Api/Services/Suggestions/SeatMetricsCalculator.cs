using AiObservatory.Data.Entities;
using NodaTime;
using NodaTime.Calendars;

namespace AiObservatory.Api.Services.Suggestions;

public sealed record SeatWeek(string Week, decimal CostUsd, double Influence);

/// <summary>
/// One seat group's totals. Mirrors <c>SeatMetrics</c> in the web app's <c>seatInfluence.ts</c>; both
/// are held to <c>docs/superpowers/contracts/seat-metrics/</c>. <c>ByWeek.Influence</c> is the
/// unique-catches-per-detailed-run pivot, the only pivot the suggestion rules need.
/// </summary>
public sealed record SeatMetrics(
    string Key,
    string Label,
    bool Mixed,
    int Runs,
    int Raised,
    int Accepted,
    int Unique,
    int Corroborated,
    int PeerAgreement,
    int Refuted,
    int Dropped,
    int Indeterminate,
    double? FalsePositiveRate,
    double? AcceptanceRate,
    decimal KnownCostUsd,
    int UnknownCostRuns,
    decimal? CostPerUnique,
    int CountsOnlyRuns,
    bool SmallSample,
    IReadOnlyList<SeatWeek> ByWeek
);

public static class SeatMetricsCalculator
{
    private const int SmallSampleRuns = 5;

    private class Tally
    {
        public int Raised;
        public int Accepted;
        public int Unique;
        public int Corroborated;
        public int Refuted;
        public int Dropped;
        public int Indeterminate;
    }

    private sealed class WeekTally : Tally
    {
        public decimal CostUsd;
        public int DetailedRuns;
    }

    private sealed class SeatTally(string model) : Tally
    {
        public string Model { get; } = model;
        public int Runs;
        public int PeerAgreement;
        public decimal KnownCostUsd;
        public decimal DetailedKnownCostUsd;
        public int UnknownCostRuns;
        public int CountsOnlyRuns;
        public SortedDictionary<string, WeekTally> Weeks { get; } = new(StringComparer.Ordinal);
    }

    public static IReadOnlyList<SeatMetrics> Compute(
        IReadOnlyList<AdversarialReviewRun> runs,
        IReadOnlyList<AdversarialReviewFinding> findings
    )
    {
        var seats = new Dictionary<string, SeatTally>(StringComparer.Ordinal);
        var reviewerRows = new Dictionary<(string RunId, string Reviewer), AdversarialReviewRun>();
        var detailedRunIds = findings.Select(f => f.RunId).ToHashSet(StringComparer.Ordinal);

        SeatTally SeatFor(string model)
        {
            if (!seats.TryGetValue(model, out var seat))
            {
                seat = new SeatTally(model);
                seats[model] = seat;
            }
            return seat;
        }

        foreach (var row in runs.Where(r => r.Role == "reviewer"))
        {
            reviewerRows.TryAdd((row.RunId, row.Reviewer), row);
            AccumulateRun(SeatFor(row.Model), row, detailedRunIds);
        }

        foreach (var finding in findings)
        {
            var raisers = finding.Participants.Where(p => p.Kind == "raiser").ToList();
            var targets = new Dictionary<string, (AdversarialReviewRun Row, HashSet<string> Reviewers)>(
                StringComparer.Ordinal
            );
            foreach (var reviewer in raisers.Select(r => r.Reviewer))
            {
                if (!reviewerRows.TryGetValue((finding.RunId, reviewer), out var row) || row.Role != "reviewer")
                {
                    continue;
                }
                if (targets.TryGetValue(row.Model, out var existing))
                {
                    existing.Reviewers.Add(reviewer);
                }
                else
                {
                    targets[row.Model] = (row, [reviewer]);
                }
            }

            foreach (var (model, target) in targets)
            {
                var seat = SeatFor(model);
                AccumulateFinding(seat, finding, raisers.Count);
                seat.PeerAgreement += finding.Participants.Count(p =>
                    p.Kind == "assessor" && p.Verdict == "agree" && !target.Reviewers.Contains(p.Reviewer)
                );
                AccumulateFinding(WeekFor(seat, target.Row.RecordedAt), finding, raisers.Count);
            }
        }

        return [.. seats.Values.OrderBy(s => Label(s.Model), StringComparer.Ordinal).Select(ToMetrics)];
    }

    private static string Label(string model) => model.Contains('+') ? $"mixed: {model}" : model;

    private static string IsoWeek(Instant instant)
    {
        var date = instant.InUtc().Date;
        return $"{WeekYearRules.Iso.GetWeekYear(date)}-W{WeekYearRules.Iso.GetWeekOfWeekYear(date):D2}";
    }

    private static WeekTally WeekFor(SeatTally seat, Instant recordedAt)
    {
        var key = IsoWeek(recordedAt);
        if (!seat.Weeks.TryGetValue(key, out var week))
        {
            week = new WeekTally();
            seat.Weeks[key] = week;
        }
        return week;
    }

    private static void AccumulateRun(SeatTally seat, AdversarialReviewRun row, HashSet<string> detailedRunIds)
    {
        var countsOnly = !detailedRunIds.Contains(row.RunId);
        seat.Runs++;
        if (countsOnly)
        {
            seat.CountsOnlyRuns++;
            seat.Raised += row.IssuesRaised;
            seat.Accepted += row.IssuesAccepted;
        }
        if (row.CostUnknown)
        {
            seat.UnknownCostRuns++;
        }
        else
        {
            seat.KnownCostUsd += row.CostUsd;
            if (!countsOnly)
            {
                seat.DetailedKnownCostUsd += row.CostUsd;
            }
        }

        var week = WeekFor(seat, row.RecordedAt);
        if (countsOnly)
        {
            week.Raised += row.IssuesRaised;
            week.Accepted += row.IssuesAccepted;
        }
        else
        {
            week.DetailedRuns++;
        }
        if (!row.CostUnknown)
        {
            week.CostUsd += row.CostUsd;
        }
    }

    private static void AccumulateFinding(Tally target, AdversarialReviewFinding finding, int raiserCount)
    {
        target.Raised++;
        switch (finding.Outcome)
        {
            case "accepted":
                target.Accepted++;
                if (raiserCount == 1)
                {
                    target.Unique++;
                }
                else
                {
                    target.Corroborated++;
                }
                break;
            case "refuted":
                target.Refuted++;
                break;
            case "dropped":
                target.Dropped++;
                break;
            default:
                target.Indeterminate++;
                break;
        }
    }

    private static SeatMetrics ToMetrics(SeatTally seat)
    {
        // Finding rows only: Accepted also carries counts-only runs' totals, which have no refuted
        // counterpart and would understate the rate for a seat with older runs.
        var settled = seat.Unique + seat.Corroborated + seat.Refuted;
        return new SeatMetrics(
            seat.Model,
            Label(seat.Model),
            seat.Model.Contains('+'),
            seat.Runs,
            seat.Raised,
            seat.Accepted,
            seat.Unique,
            seat.Corroborated,
            seat.PeerAgreement,
            seat.Refuted,
            seat.Dropped,
            seat.Indeterminate,
            settled == 0 ? null : (double)seat.Refuted / settled,
            seat.Raised == 0 ? null : (double)seat.Accepted / seat.Raised,
            seat.KnownCostUsd,
            seat.UnknownCostRuns,
            seat.Unique == 0 || seat.DetailedKnownCostUsd == 0 ? null : seat.DetailedKnownCostUsd / seat.Unique,
            seat.CountsOnlyRuns,
            seat.Runs < SmallSampleRuns,
            [
                .. seat.Weeks.Select(w => new SeatWeek(
                    w.Key,
                    w.Value.CostUsd,
                    w.Value.DetailedRuns == 0 ? 0 : (double)w.Value.Unique / w.Value.DetailedRuns
                )),
            ]
        );
    }
}
