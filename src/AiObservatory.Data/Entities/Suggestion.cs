using NodaTime;

namespace AiObservatory.Data.Entities;

/// <summary>
/// One suggestion produced by a rule for one subject. <see cref="Key"/> (rule id and subject) is the
/// stable identity: the same condition updates this row across evaluations instead of adding another.
/// </summary>
public sealed class Suggestion
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Key { get; set; } = "";
    public string RuleId { get; init; } = "";
    public string Subject { get; init; } = "";

    /// <summary>"info" or "attention".</summary>
    public string Severity { get; set; } = "attention";

    public string Title { get; set; } = "";

    /// <summary>The figures, window, sample size and run ids behind the suggestion (jsonb).</summary>
    public string EvidenceJson { get; set; } = "{}";

    /// <summary>"open", "dismissed", "snoozed" or "resolved".</summary>
    public string Status { get; set; } = "open";

    public Instant? SnoozedUntil { get; set; }
    public Instant FirstSeenAt { get; set; }
    public Instant LastSeenAt { get; set; }
    public Instant LastEvaluatedAt { get; set; }

    /// <summary>The primary figure and sample size at creation or at the last dismiss or snooze (jsonb).</summary>
    public string BaselineFiguresJson { get; set; } = "{}";
}

/// <summary>The latest result of one rule, so "not evaluated" can be shown with its reason.</summary>
public sealed class SuggestionRuleRun
{
    public string RuleId { get; init; } = "";
    public Instant EvaluatedAt { get; set; }

    /// <summary>"evaluated" or "notEvaluated".</summary>
    public string Status { get; set; } = "evaluated";

    public string? Reason { get; set; }
    public int SubjectsEvaluated { get; set; }
    public int SuggestionsEmitted { get; set; }
}
