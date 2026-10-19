using System.Globalization;
using System.Text.Json;
using AiObservatory.Data.Entities;
using NodaTime;
using NodaTime.Text;

namespace AiObservatory.Api.Services.Suggestions;

public sealed record ReconcileResult(IReadOnlyList<Suggestion> Inserts, IReadOnlyList<SuggestionRuleRun> RuleRuns);

/// <summary>
/// Pure reconciliation of rule outcomes against the stored suggestions. Existing rows are mutated in
/// place (they are tracked entities); new rows are returned for insertion. No I/O, so the whole
/// lifecycle is unit-testable.
/// </summary>
public static class SuggestionLifecycle
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const double ZeroTolerance = 1e-12;

    private sealed record Baseline(string Label, double Primary, int SampleSize);

    public static string KeyOf(string ruleId, string subject) => $"{ruleId}:{subject}";

    // Column widths: a long title or joined skip reasons must never fail the evaluation's insert.
    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max];

    public static Suggestion NewRow(SuggestionCandidate candidate, Instant now) =>
        new()
        {
            Key = KeyOf(candidate.RuleId, candidate.Subject),
            RuleId = candidate.RuleId,
            Subject = candidate.Subject,
            Severity = candidate.Severity,
            Title = Clip(candidate.Title, 300),
            EvidenceJson = SerializeEvidence(candidate.Evidence),
            Status = "open",
            FirstSeenAt = now,
            LastSeenAt = now,
            LastEvaluatedAt = now,
            BaselineFiguresJson = BaselineOf(candidate),
        };

    public static ReconcileResult Reconcile(
        IReadOnlyList<Suggestion> existing,
        IReadOnlyList<RuleOutcome> outcomes,
        Instant now,
        SuggestionOptions options
    )
    {
        var byKey = existing.ToDictionary(s => s.Key, StringComparer.Ordinal);
        var inserts = new List<Suggestion>();
        var ruleRuns = new List<SuggestionRuleRun>();

        foreach (var outcome in outcomes)
        {
            ruleRuns.Add(
                new SuggestionRuleRun
                {
                    RuleId = outcome.RuleId,
                    EvaluatedAt = now,
                    Status = outcome.Evaluated ? "evaluated" : "notEvaluated",
                    Reason = outcome.Reason is null ? null : Clip(outcome.Reason, 500),
                    SubjectsEvaluated = outcome.SubjectsEvaluated,
                    SuggestionsEmitted = outcome.Candidates.Count,
                }
            );
            // An unevaluated rule says nothing about its subjects, so its rows are left as they are.
            if (outcome.Evaluated)
            {
                ApplyOutcome(outcome, existing, byKey, inserts, now, options);
            }
        }
        return new ReconcileResult(inserts, ruleRuns);
    }

    private static void ApplyOutcome(
        RuleOutcome outcome,
        IReadOnlyList<Suggestion> existing,
        Dictionary<string, Suggestion> byKey,
        List<Suggestion> inserts,
        Instant now,
        SuggestionOptions options
    )
    {
        var firing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in outcome.Candidates)
        {
            var key = KeyOf(candidate.RuleId, candidate.Subject);
            firing.Add(key);
            if (byKey.TryGetValue(key, out var row))
            {
                Refresh(row, candidate, now, options);
            }
            else
            {
                inserts.Add(NewRow(candidate, now));
            }
        }

        foreach (
            var row in existing.Where(s =>
                s.RuleId == outcome.RuleId
                && !firing.Contains(s.Key)
                && (outcome.JudgedSubjects is null || outcome.JudgedSubjects.Contains(s.Subject))
            )
        )
        {
            row.LastEvaluatedAt = now;
            if (row.Status is "open" or "snoozed" or "dismissed")
            {
                row.Status = "resolved";
            }
        }
    }

    private static void Refresh(Suggestion row, SuggestionCandidate candidate, Instant now, SuggestionOptions options)
    {
        row.Severity = candidate.Severity;
        row.Title = Clip(candidate.Title, 300);
        row.EvidenceJson = SerializeEvidence(candidate.Evidence);
        row.LastSeenAt = now;
        row.LastEvaluatedAt = now;

        var wasResolved = row.Status == "resolved";
        var reopen = row.Status switch
        {
            "snoozed" => row.SnoozedUntil is null || row.SnoozedUntil <= now,
            "dismissed" => Moved(row.BaselineFiguresJson, candidate, options.ReopenChangeFraction),
            "resolved" => true,
            _ => false,
        };
        if (!reopen)
        {
            return;
        }

        Reopen(row, candidate);
        if (wasResolved)
        {
            row.FirstSeenAt = now;
        }
    }

    private static void Reopen(Suggestion row, SuggestionCandidate candidate)
    {
        row.Status = "open";
        row.SnoozedUntil = null;
        row.BaselineFiguresJson = BaselineOf(candidate);
    }

    public static void Dismiss(Suggestion row)
    {
        row.Status = "dismissed";
        row.SnoozedUntil = null;
        row.BaselineFiguresJson = BaselineFromStored(row);
    }

    public static void Snooze(Suggestion row, int days, Instant now)
    {
        row.Status = "snoozed";
        row.SnoozedUntil = now.Plus(Duration.FromDays(days));
        row.BaselineFiguresJson = BaselineFromStored(row);
    }

    // A dismissed suggestion comes back only when its primary figure moved by more than the fraction
    // against the baseline (a zero baseline with a non-zero figure counts), or its sample size doubled.
    private static bool Moved(string baselineJson, SuggestionCandidate candidate, double fraction)
    {
        var baseline = JsonSerializer.Deserialize<Baseline>(baselineJson, Json);
        if (baseline is null)
        {
            return true;
        }
        var primary = PrimaryOf(candidate.Evidence, candidate.PrimaryFigureLabel);
        var primaryMoved =
            Math.Abs(baseline.Primary) < ZeroTolerance
                ? Math.Abs(primary) >= ZeroTolerance
                : Math.Abs(primary - baseline.Primary) > fraction * Math.Abs(baseline.Primary);
        var sampleDoubled = baseline.SampleSize > 0 && candidate.Evidence.SampleSize >= 2 * baseline.SampleSize;
        return primaryMoved || sampleDoubled;
    }

    private static double PrimaryOf(SuggestionEvidence evidence, string label)
    {
        var figure = evidence.Figures.FirstOrDefault(f => f.Label == label);
        return figure?.Value is IConvertible and not string
            ? Convert.ToDouble(figure.Value, CultureInfo.InvariantCulture)
            : 0;
    }

    private static string BaselineOf(SuggestionCandidate candidate) =>
        JsonSerializer.Serialize(
            new Baseline(
                candidate.PrimaryFigureLabel,
                PrimaryOf(candidate.Evidence, candidate.PrimaryFigureLabel),
                candidate.Evidence.SampleSize
            ),
            Json
        );

    // Dismiss and snooze re-baseline against the evidence the owner was looking at.
    private static string BaselineFromStored(Suggestion row)
    {
        var label = JsonSerializer.Deserialize<Baseline>(row.BaselineFiguresJson, Json)?.Label ?? "";
        using var evidence = JsonDocument.Parse(row.EvidenceJson);
        var primary = 0d;
        if (evidence.RootElement.TryGetProperty("figures", out var figures))
        {
            foreach (
                var figure in figures
                    .EnumerateArray()
                    .Where(f =>
                        f.GetProperty("label").GetString() == label
                        && f.GetProperty("value").ValueKind == JsonValueKind.Number
                    )
            )
            {
                primary = figure.GetProperty("value").GetDouble();
            }
        }
        var sample = evidence.RootElement.TryGetProperty("sampleSize", out var s) ? s.GetInt32() : 0;
        return JsonSerializer.Serialize(new Baseline(label, primary, sample), Json);
    }

    public static string SerializeEvidence(SuggestionEvidence evidence) =>
        JsonSerializer.Serialize(
            new
            {
                window = new { from = Iso(evidence.Window.From), to = Iso(evidence.Window.To) },
                sampleSize = evidence.SampleSize,
                figures = evidence.Figures.Select(f => new
                {
                    label = f.Label,
                    value = f.Value,
                    unit = f.Unit,
                }),
                runIds = evidence.RunIds,
            },
            Json
        );

    private static string Iso(LocalDate date) => LocalDatePattern.Iso.Format(date);
}
