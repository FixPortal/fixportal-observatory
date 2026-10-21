using System.Text.Json;
using AiObservatory.Api.Services.Suggestions;
using AiObservatory.Data.Entities;
using AwesomeAssertions;
using NodaTime;
using NodaTime.Text;

namespace AiObservatory.Api.Tests.Suggestions;

public class SeatMetricsCalculatorTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record RunJson(
        string Reviewer,
        string Model,
        string Role,
        decimal CostUsd,
        bool CostUnknown,
        int IssuesRaised,
        int IssuesAccepted,
        string RunId,
        string RecordedAt
    );

    private sealed record ParticipantJson(string Reviewer, string Model, string? Verdict = null);

    private sealed record FindingJson(
        string RunId,
        string ChunkId,
        string FindingId,
        string Severity,
        string Outcome,
        List<ParticipantJson> RaisedBy,
        List<ParticipantJson> Assessments
    );

    private sealed record FixtureInput(List<RunJson> Runs, List<FindingJson> Findings);

    internal static string ContractDirectory(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "superpowers", "contracts", name);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException($"contract fixtures '{name}' not found above the test binaries");
    }

    internal static AdversarialReviewRun ToRun(
        string reviewer,
        string model,
        string role,
        decimal costUsd,
        bool costUnknown,
        int raised,
        int accepted,
        string runId,
        Instant recordedAt
    ) =>
        new()
        {
            Reviewer = reviewer,
            Model = model,
            Role = role,
            CostUsd = costUsd,
            CostUnknown = costUnknown,
            IssuesRaised = raised,
            IssuesAccepted = accepted,
            RunId = runId,
            RecordedAt = recordedAt,
        };

    internal static AdversarialReviewFinding ToFinding(
        string runId,
        string findingId,
        string outcome,
        IEnumerable<(string Reviewer, string Model)> raisers,
        IEnumerable<(string Reviewer, string Model, string Verdict)>? assessors = null,
        string chunkId = "single"
    )
    {
        var id = Guid.NewGuid();
        return new AdversarialReviewFinding
        {
            Id = id,
            RunId = runId,
            ChunkId = chunkId,
            FindingId = findingId,
            Severity = "medium",
            Outcome = outcome,
            Participants =
            [
                .. raisers.Select(r => new AdversarialReviewFindingParticipant
                {
                    FindingId = id,
                    Reviewer = r.Reviewer,
                    Model = r.Model,
                    Kind = "raiser",
                }),
                .. (assessors ?? []).Select(a => new AdversarialReviewFindingParticipant
                {
                    FindingId = id,
                    Reviewer = a.Reviewer,
                    Model = a.Model,
                    Kind = "assessor",
                    Verdict = a.Verdict,
                }),
            ],
        };
    }

    [Fact]
    public async Task Matches_the_shared_seat_metrics_contract_fixture()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = ContractDirectory("seat-metrics");
        var input = JsonSerializer.Deserialize<FixtureInput>(
            await File.ReadAllTextAsync(Path.Combine(dir, "input.json"), ct),
            Json
        )!;
        var expected = JsonSerializer.Deserialize<List<SeatMetrics>>(
            await File.ReadAllTextAsync(Path.Combine(dir, "expected.json"), ct),
            Json
        )!;
        var runs = input
            .Runs.Select(r =>
                ToRun(
                    r.Reviewer,
                    r.Model,
                    r.Role,
                    r.CostUsd,
                    r.CostUnknown,
                    r.IssuesRaised,
                    r.IssuesAccepted,
                    r.RunId,
                    InstantPattern.ExtendedIso.Parse(r.RecordedAt).Value
                )
            )
            .ToList();
        var findings = input
            .Findings.Select(f =>
                ToFinding(
                    f.RunId,
                    f.FindingId,
                    f.Outcome,
                    f.RaisedBy.Select(p => (p.Reviewer, p.Model)),
                    f.Assessments.Select(a => (a.Reviewer, a.Model, a.Verdict!)),
                    f.ChunkId
                )
            )
            .ToList();

        var actual = SeatMetricsCalculator.Compute(runs, findings);

        actual.Select(a => a.Key).Should().Equal(expected.Select(e => e.Key));
        foreach (var (a, e) in actual.Zip(expected))
        {
            AssertSame(a, e);
        }
    }

    private static void AssertSame(SeatMetrics a, SeatMetrics e)
    {
        var why = $"seat {e.Key}";
        a.Label.Should().Be(e.Label, why);
        a.Mixed.Should().Be(e.Mixed, why);
        a.Runs.Should().Be(e.Runs, why);
        a.Raised.Should().Be(e.Raised, why);
        a.Accepted.Should().Be(e.Accepted, why);
        a.Unique.Should().Be(e.Unique, why);
        a.Corroborated.Should().Be(e.Corroborated, why);
        a.PeerAgreement.Should().Be(e.PeerAgreement, why);
        a.Refuted.Should().Be(e.Refuted, why);
        a.Dropped.Should().Be(e.Dropped, why);
        a.Indeterminate.Should().Be(e.Indeterminate, why);
        a.KnownCostUsd.Should().Be(e.KnownCostUsd, why);
        a.UnknownCostRuns.Should().Be(e.UnknownCostRuns, why);
        a.CostPerUnique.Should().Be(e.CostPerUnique, why);
        a.CountsOnlyRuns.Should().Be(e.CountsOnlyRuns, why);
        a.SmallSample.Should().Be(e.SmallSample, why);
        AssertRate(a.FalsePositiveRate, e.FalsePositiveRate, why + " falsePositiveRate");
        AssertRate(a.AcceptanceRate, e.AcceptanceRate, why + " acceptanceRate");
        a.ByWeek.Select(w => w.Week).Should().Equal(e.ByWeek.Select(w => w.Week), why);
        foreach (var (aw, ew) in a.ByWeek.Zip(e.ByWeek))
        {
            aw.CostUsd.Should().Be(ew.CostUsd, why + " " + ew.Week);
            aw.Influence.Should().BeApproximately(ew.Influence, 1e-9, why + " " + ew.Week);
        }
    }

    private static void AssertRate(double? actual, double? expected, string because)
    {
        if (expected is null)
        {
            actual.Should().BeNull(because);
            return;
        }
        actual.Should().NotBeNull(because);
        actual.Value.Should().BeApproximately(expected.Value, 1e-9, because);
    }

    [Fact]
    public void A_seat_that_only_has_counts_only_history_has_no_detailed_runs_or_catches()
    {
        var runs = new[]
        {
            ToRun("zai", "glm-5.3", "reviewer", 1m, false, 6, 4, "OLD", Instant.FromUtc(2026, 9, 1, 12, 0)),
        };

        var seat = SeatMetricsCalculator.Compute(runs, []).Single();

        seat.CountsOnlyRuns.Should().Be(1);
        seat.Runs.Should().Be(1);
        seat.Unique.Should().Be(0);
        seat.Corroborated.Should().Be(0);
        seat.CostPerUnique.Should().BeNull();
    }

    [Fact]
    public void Cost_per_unique_catch_divides_known_cost_by_catches_of_known_cost_runs_only()
    {
        // $10 known-cost run with 1 unique catch, plus an unknown-cost run with 2: $10 per catch, not $3.33.
        var at = Instant.FromUtc(2026, 10, 1, 12, 0);
        var runs = new[]
        {
            ToRun("a", "m", "reviewer", 10m, false, 0, 0, "K", at),
            ToRun("a", "m", "reviewer", 0m, true, 0, 0, "U", at),
        };
        var findings = new[]
        {
            ToFinding("K", "F1", "accepted", [("a", "m")]),
            ToFinding("U", "F2", "accepted", [("a", "m")]),
            ToFinding("U", "F3", "accepted", [("a", "m")]),
        };

        SeatMetricsCalculator.Compute(runs, findings).Single().CostPerUnique.Should().Be(10m);
    }
}
