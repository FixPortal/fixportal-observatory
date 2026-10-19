using AiObservatory.Api.Services.Suggestions;
using AiObservatory.Data.Entities;
using AwesomeAssertions;
using NodaTime;

namespace AiObservatory.Api.Tests.Suggestions;

public class SuggestionLifecycleTests
{
    private const string Rule = "seat.no-unique-catch";
    private const string Primary = "Detailed runs";
    private static readonly SuggestionOptions Options = new();
    private static readonly Instant Now = Instant.FromUtc(2026, 10, 19, 0, 5);

    private static SuggestionCandidate Candidate(double primary = 10, int sample = 10, string subject = "glm-5.3") =>
        new(
            Rule,
            subject,
            "attention",
            "Seat glm-5.3 added no unique accepted catch",
            new SuggestionEvidence(
                new DateWindow(new LocalDate(2026, 8, 21), new LocalDate(2026, 10, 18)),
                sample,
                [new SuggestionFigure(Primary, primary, "runs")],
                ["R1"]
            ),
            Primary
        );

    private static RuleOutcome Fires(params SuggestionCandidate[] candidates) =>
        new(Rule, candidates, true, null, Math.Max(1, candidates.Length));

    private static RuleOutcome Quiet() => new(Rule, [], true, null, 1);

    private static Suggestion Existing(
        string status,
        double primary = 10,
        int sample = 10,
        Instant? snoozedUntil = null
    )
    {
        var candidate = Candidate(primary, sample);
        var row = SuggestionLifecycle.NewRow(candidate, Now.Plus(Duration.FromDays(-7)));
        row.Status = status;
        row.SnoozedUntil = snoozedUntil;
        return row;
    }

    [Fact]
    public void A_first_firing_inserts_an_open_row_with_a_baseline()
    {
        var result = SuggestionLifecycle.Reconcile([], [Fires(Candidate())], Now, Options);

        var row = result.Inserts.Should().ContainSingle().Subject;
        row.Key.Should().Be("seat.no-unique-catch:glm-5.3");
        row.Status.Should().Be("open");
        row.FirstSeenAt.Should().Be(Now);
        row.LastSeenAt.Should().Be(Now);
        row.BaselineFiguresJson.Should().Contain("\"label\":\"Detailed runs\"").And.Contain("\"primary\":10");
        row.EvidenceJson.Should().Contain("\"sampleSize\":10").And.Contain("\"from\":\"2026-08-21\"");
    }

    [Fact]
    public void A_second_firing_updates_the_same_row_and_keeps_its_first_seen_time()
    {
        var existing = Existing("open");
        var first = existing.FirstSeenAt;

        var result = SuggestionLifecycle.Reconcile(
            [existing],
            [Fires(Candidate(primary: 12, sample: 12))],
            Now,
            Options
        );

        result.Inserts.Should().BeEmpty();
        existing.Status.Should().Be("open");
        existing.FirstSeenAt.Should().Be(first);
        existing.LastSeenAt.Should().Be(Now);
        existing.EvidenceJson.Should().Contain("\"sampleSize\":12");
    }

    [Fact]
    public void A_condition_that_no_longer_holds_resolves_open_and_snoozed_rows_of_an_evaluated_rule()
    {
        var open = Existing("open");
        var snoozed = Existing("snoozed", snoozedUntil: Now.Plus(Duration.FromDays(5)));
        snoozed.Key = "seat.no-unique-catch:other";

        SuggestionLifecycle.Reconcile([open, snoozed], [Quiet()], Now, Options);

        open.Status.Should().Be("resolved");
        snoozed.Status.Should().Be("resolved");
    }

    [Fact]
    public void A_rule_that_was_not_evaluated_leaves_its_rows_untouched()
    {
        var open = Existing("open");

        SuggestionLifecycle.Reconcile([open], [RuleOutcome.NotEvaluated(Rule, "No seat has 5 runs yet")], Now, Options);

        open.Status.Should().Be("open");
        open.LastSeenAt.Should().Be(Now.Plus(Duration.FromDays(-7)));
    }

    [Fact]
    public void A_resolved_row_that_fires_again_reopens_with_a_reset_first_seen_time_and_baseline()
    {
        var resolved = Existing("resolved");

        SuggestionLifecycle.Reconcile([resolved], [Fires(Candidate(primary: 20, sample: 20))], Now, Options);

        resolved.Status.Should().Be("open");
        resolved.FirstSeenAt.Should().Be(Now);
        resolved.BaselineFiguresJson.Should().Contain("\"primary\":20");
    }

    [Theory]
    [InlineData(11, 10, "dismissed")] // 10% move: stays dismissed
    [InlineData(12.5, 10, "dismissed")] // exactly 25%: not more than 25%
    [InlineData(13, 10, "open")] // 30% move: reopens
    [InlineData(10, 20, "open")] // sample doubled: reopens
    [InlineData(10, 19, "dismissed")] // sample short of double
    public void A_dismissed_row_reopens_only_on_a_material_change(double primary, int sample, string expected)
    {
        var dismissed = Existing("dismissed", primary: 10, sample: 10);

        SuggestionLifecycle.Reconcile([dismissed], [Fires(Candidate(primary, sample))], Now, Options);

        dismissed.Status.Should().Be(expected);
    }

    [Fact]
    public void A_zero_baseline_with_a_non_zero_figure_counts_as_a_material_change()
    {
        var dismissed = Existing("dismissed", primary: 0, sample: 10);

        SuggestionLifecycle.Reconcile([dismissed], [Fires(Candidate(primary: 1, sample: 10))], Now, Options);

        dismissed.Status.Should().Be("open");
    }

    [Fact]
    public void A_snoozed_row_stays_snoozed_until_its_date_then_reopens_on_an_unchanged_condition()
    {
        var snoozed = Existing("snoozed", snoozedUntil: Now.Plus(Duration.FromDays(3)));
        SuggestionLifecycle.Reconcile([snoozed], [Fires(Candidate())], Now, Options);
        snoozed.Status.Should().Be("snoozed");

        var expired = Existing("snoozed", snoozedUntil: Now.Plus(Duration.FromDays(-1)));
        SuggestionLifecycle.Reconcile([expired], [Fires(Candidate())], Now, Options);
        expired.Status.Should().Be("open");
        expired.SnoozedUntil.Should().BeNull();
    }

    [Fact]
    public void Dismiss_and_snooze_record_the_current_evidence_as_the_new_baseline()
    {
        var row = Existing("open", primary: 10, sample: 10);
        SuggestionLifecycle.Reconcile([row], [Fires(Candidate(primary: 14, sample: 14))], Now, Options);
        row.BaselineFiguresJson.Should().Contain("\"primary\":10");

        SuggestionLifecycle.Dismiss(row);

        row.Status.Should().Be("dismissed");
        row.BaselineFiguresJson.Should().Contain("\"primary\":14").And.Contain("\"sampleSize\":14");

        var other = Existing("open", primary: 10, sample: 10);
        SuggestionLifecycle.Snooze(other, 7, Now);
        other.Status.Should().Be("snoozed");
        other.SnoozedUntil.Should().Be(Now.Plus(Duration.FromDays(7)));
    }

    [Fact]
    public void A_dismissed_row_that_stops_firing_resolves_so_a_return_reopens_it()
    {
        var row = Existing("dismissed", primary: 10, sample: 10);

        SuggestionLifecycle.Reconcile([row], [Quiet()], Now, Options);
        row.Status.Should().Be("resolved");

        SuggestionLifecycle.Reconcile([row], [Fires(Candidate(primary: 10, sample: 10))], Now, Options);
        row.Status.Should().Be("open");
    }

    [Fact]
    public void Rule_runs_record_the_latest_result_of_every_rule()
    {
        var result = SuggestionLifecycle.Reconcile(
            [],
            [Fires(Candidate()), RuleOutcome.NotEvaluated("seat.cost-outlier", "Fewer than 3 peers")],
            Now,
            Options
        );

        result
            .RuleRuns.Should()
            .Contain(r =>
                r.RuleId == Rule && r.Status == "evaluated" && r.SuggestionsEmitted == 1 && r.EvaluatedAt == Now
            );
        result
            .RuleRuns.Should()
            .Contain(r =>
                r.RuleId == "seat.cost-outlier" && r.Status == "notEvaluated" && r.Reason == "Fewer than 3 peers"
            );
    }
}
