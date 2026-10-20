using AiObservatory.Api.Services.Suggestions;
using AiObservatory.Data.Entities;
using AwesomeAssertions;
using NodaTime;

namespace AiObservatory.Api.Tests.Suggestions;

public class SeatRulesTests
{
    private static readonly SuggestionOptions Options = new();
    private static readonly Instant At = Instant.FromUtc(2026, 10, 12, 12, 0);

    private static AdversarialReviewRun Run(
        string reviewer,
        string model,
        string runId,
        decimal cost = 1m,
        bool unknown = false,
        int raised = 0,
        int accepted = 0
    ) => SeatMetricsCalculatorTests.ToRun(reviewer, model, "reviewer", cost, unknown, raised, accepted, runId, At);

    private static AdversarialReviewFinding Finding(string runId, string id, string outcome, params string[] raisers) =>
        SeatMetricsCalculatorTests.ToFinding(runId, id, outcome, raisers.Select(r => (r, ModelOf(r))));

    private static string ModelOf(string reviewer) => "model-" + reviewer;

    private static SuggestionInputs Inputs(
        IEnumerable<AdversarialReviewRun> runs,
        IEnumerable<AdversarialReviewFinding> findings
    ) =>
        new(
            new DateWindow(new LocalDate(2026, 8, 20), new LocalDate(2026, 10, 18)),
            new DateWindow(new LocalDate(2026, 9, 19), new LocalDate(2026, 10, 18)),
            new LocalDate(2026, 10, 19),
            [.. runs],
            [.. findings],
            [],
            [],
            0.79m
        );

    // n runs, each with a row for every reviewer.
    private static IEnumerable<AdversarialReviewRun> Rows(int runCount, params string[] reviewers) =>
        Enumerable.Range(1, runCount).SelectMany(i => reviewers.Select(r => Run(r, ModelOf(r), $"R{i}")));

    [Fact]
    public void NoUniqueCatch_fires_for_the_seat_with_no_unique_catch_only()
    {
        // Both seats are corroborated on F1 in every run; b also has a unique catch in R1.
        var findings = Enumerable
            .Range(1, 5)
            .Select(i => Finding($"R{i}", "F1", "accepted", "a", "b"))
            .Append(Finding("R1", "F2", "accepted", "b"));

        var outcome = new SeatNoUniqueCatchRule().Evaluate(Inputs(Rows(5, "a", "b"), findings), Options);

        outcome.Evaluated.Should().BeTrue();
        outcome.SubjectsEvaluated.Should().Be(2);
        var candidate = outcome.Candidates.Should().ContainSingle().Subject;
        candidate.Subject.Should().Be("model-a");
        candidate.RuleId.Should().Be("seat.no-unique-catch");
        candidate.Severity.Should().Be("attention");
        candidate.Evidence.SampleSize.Should().Be(5);
    }

    [Fact]
    public void NoUniqueCatch_below_the_run_floor_is_not_evaluated_with_a_reason()
    {
        var findings = Enumerable.Range(1, 4).Select(i => Finding($"R{i}", "F1", "accepted", "a", "b"));

        var outcome = new SeatNoUniqueCatchRule().Evaluate(Inputs(Rows(4, "a", "b"), findings), Options);

        outcome.Evaluated.Should().BeFalse();
        outcome.Candidates.Should().BeEmpty();
        outcome.Reason.Should().Contain("5");
    }

    [Fact]
    public void Seat_rules_never_fire_from_counts_only_history()
    {
        // Ten older runs with accepted counts but no finding rows at all.
        var runs = Enumerable.Range(1, 10).Select(i => Run("a", "model-a", $"OLD{i}", raised: 4, accepted: 3));
        var inputs = Inputs(runs, []);

        new SeatNoUniqueCatchRule().Evaluate(inputs, Options).Evaluated.Should().BeFalse();
        new SeatConfirmationOnlyRule().Evaluate(inputs, Options).Evaluated.Should().BeFalse();
        new SeatHighFalsePositiveRule().Evaluate(inputs, Options).Evaluated.Should().BeFalse();
    }

    [Fact]
    public void A_mixed_history_seat_counts_only_its_detailed_runs_against_the_floor()
    {
        // Three counts-only runs plus three detailed ones: six runs, but only three are detailed.
        var runs = Enumerable
            .Range(1, 3)
            .Select(i => Run("a", "model-a", $"OLD{i}", raised: 4, accepted: 3))
            .Concat(Rows(3, "a", "b"));
        var findings = Enumerable.Range(1, 3).Select(i => Finding($"R{i}", "F1", "accepted", "a", "b"));

        var outcome = new SeatNoUniqueCatchRule().Evaluate(Inputs(runs, findings), Options);

        outcome.Evaluated.Should().BeFalse();
        outcome.Candidates.Should().BeEmpty();
    }

    [Theory]
    [InlineData(7, 3, true)] // 7 refuted of 10 settled: 0.7 is above 0.5
    [InlineData(5, 5, false)] // exactly 0.5 is not above the threshold
    public void HighFalsePositive_fires_only_above_the_threshold(int refuted, int accepted, bool fires)
    {
        var findings = Enumerable
            .Range(1, refuted)
            .Select(i => Finding("R1", $"X{i}", "refuted", "a"))
            .Concat(Enumerable.Range(1, accepted).Select(i => Finding("R1", $"Y{i}", "accepted", "a")));

        var outcome = new SeatHighFalsePositiveRule().Evaluate(Inputs(Rows(1, "a"), findings), Options);

        outcome.Evaluated.Should().BeTrue();
        outcome.Candidates.Any().Should().Be(fires);
    }

    [Fact]
    public void HighFalsePositive_below_ten_settled_findings_is_not_evaluated()
    {
        var findings = Enumerable.Range(1, 9).Select(i => Finding("R1", $"X{i}", "refuted", "a"));

        new SeatHighFalsePositiveRule().Evaluate(Inputs(Rows(1, "a"), findings), Options).Evaluated.Should().BeFalse();
    }

    [Fact]
    public void ConfirmationOnly_fires_when_every_catch_was_corroborated()
    {
        var findings = Enumerable.Range(1, 10).Select(i => Finding("R1", $"F{i}", "accepted", "a", "b"));

        var outcome = new SeatConfirmationOnlyRule().Evaluate(Inputs(Rows(1, "a", "b"), findings), Options);

        outcome.Candidates.Select(c => c.Subject).Should().BeEquivalentTo("model-a", "model-b");
        outcome.Candidates.Should().OnlyContain(c => c.Severity == "info");
    }

    [Fact]
    public void ConfirmationOnly_does_not_fire_with_a_unique_catch_and_waits_for_ten_catches()
    {
        // a has ten catches in total but one of them is unique; b has ten, all corroborated.
        var withUnique = Enumerable
            .Range(1, 10)
            .Select(i => Finding("R1", $"F{i}", "accepted", "a", "b"))
            .Append(Finding("R1", "U1", "accepted", "a"));
        new SeatConfirmationOnlyRule()
            .Evaluate(Inputs(Rows(1, "a", "b"), withUnique), Options)
            .Candidates.Select(c => c.Subject)
            .Should()
            .BeEquivalentTo("model-b");

        var nine = Enumerable.Range(1, 9).Select(i => Finding("R1", $"F{i}", "accepted", "a", "b"));
        new SeatConfirmationOnlyRule().Evaluate(Inputs(Rows(1, "a", "b"), nine), Options).Evaluated.Should().BeFalse();
    }

    // Four seats, three detailed runs each, one unique catch each; the cost per run differs.
    private static SuggestionInputs OutlierWorld(decimal subjectCost, bool subjectHasUnique = true)
    {
        var costs = new Dictionary<string, decimal>
        {
            ["a"] = 1m,
            ["b"] = 1m,
            ["c"] = 1m,
            ["s"] = subjectCost,
        };
        var runs = costs.SelectMany(kv =>
            Enumerable.Range(1, 3).Select(i => Run(kv.Key, ModelOf(kv.Key), $"R{i}", kv.Value))
        );
        var findings = new List<AdversarialReviewFinding>();
        foreach (var reviewer in costs.Keys.Where(k => k != "s" || subjectHasUnique))
        {
            findings.Add(Finding("R1", $"U-{reviewer}", "accepted", reviewer));
        }
        // Make runs 2 and 3 detailed for everyone with a dropped finding that no seat is judged on.
        findings.Add(Finding("R2", "D1", "dropped", "a"));
        findings.Add(Finding("R3", "D1", "dropped", "a"));
        return Inputs(runs, findings);
    }

    [Fact]
    public void CostOutlier_fires_for_the_seat_far_above_the_peer_median()
    {
        // Peers cost 3 per unique catch (three runs at 1); the subject costs 30.
        var outcome = new SeatCostOutlierRule().Evaluate(OutlierWorld(10m), Options);

        outcome.Evaluated.Should().BeTrue();
        outcome.Candidates.Should().ContainSingle().Which.Subject.Should().Be("model-s");
    }

    [Fact]
    public void CostOutlier_needs_three_peers_with_a_value()
    {
        // Only two peers remain once one seat is dropped from the world.
        var world = OutlierWorld(10m);
        var smaller = world with { Runs = [.. world.Runs.Where(r => r.Reviewer != "c")] };

        new SeatCostOutlierRule().Evaluate(smaller, Options).Evaluated.Should().BeFalse();
    }

    [Fact]
    public void CostOutlier_with_a_subject_that_has_no_unique_catch_is_never_that_subject()
    {
        // The would-be outlier has no unique catch, so it has no cost per unique catch to compare.
        var outcome = new SeatCostOutlierRule().Evaluate(OutlierWorld(10m, subjectHasUnique: false), Options);

        outcome.Candidates.Should().BeEmpty();
        // Peers with a null value are excluded from the median, leaving only two peers for each seat.
        outcome.Evaluated.Should().BeFalse();
    }

    [Fact]
    public void HighFalsePositive_states_its_sample_size_as_settled_findings()
    {
        var findings = Enumerable.Range(1, 12).Select(i => Finding("R1", $"X{i}", "refuted", "a"));

        var candidate = new SeatHighFalsePositiveRule()
            .Evaluate(Inputs(Rows(1, "a"), findings), Options)
            .Candidates.Should()
            .ContainSingle()
            .Subject;

        candidate.Evidence.SampleSize.Should().Be(12);
    }

    [Fact]
    public void ConfirmationOnly_states_its_sample_size_as_accepted_catches()
    {
        var findings = Enumerable.Range(1, 10).Select(i => Finding("R1", $"F{i}", "accepted", "a", "b"));

        var candidate = new SeatConfirmationOnlyRule()
            .Evaluate(Inputs(Rows(1, "a", "b"), findings), Options)
            .Candidates[0];

        candidate.Evidence.SampleSize.Should().Be(10);
    }

    [Fact]
    public void Evidence_run_ids_list_only_detailed_runs()
    {
        var runs = Rows(5, "a", "b").Append(Run("a", "model-a", "OLD1", raised: 4, accepted: 3));
        var findings = Enumerable
            .Range(1, 5)
            .Select(i => Finding($"R{i}", "F1", "accepted", "a", "b"))
            .Append(Finding("R1", "F2", "accepted", "b"));

        var candidate = new SeatNoUniqueCatchRule()
            .Evaluate(Inputs(runs, findings), Options)
            .Candidates.Should()
            .ContainSingle()
            .Subject;

        candidate.Evidence.RunIds.Should().NotContain("OLD1");
        candidate.Evidence.RunIds.Should().HaveCount(candidate.Evidence.SampleSize);
    }
}
