using System.Text.Json;
using AiObservatory.Data.Entities;
using NodaTime;

namespace AiObservatory.Api.Services.Intelligence;

/// <summary>
/// The provider/model strings from one day's aggregates and subscriptions that insight
/// subject matching is grounded in. Models and providers are matched differently: a shared
/// model is required whenever either insight names one, and provider-only matching is the
/// fallback for when neither does — provider names appear in nearly every LLM-authored
/// insight, so matching on them alone suppresses genuinely new stories about other models.
/// </summary>
public sealed record InsightKnownSubjects(IReadOnlyCollection<string> Models, IReadOnlyCollection<string> Providers);

/// <summary>
/// Suppresses a newly generated insight when an unacknowledged insight of the same
/// <see cref="InsightType"/>, about at least one of the same models, was generated within
/// <see cref="StalenessWindow"/>. The daily regeneration pass builds a fresh prompt from a
/// rolling 30-day window every time, so without this an ongoing story (e.g. one model
/// dominating spend) gets restated as a "new" card every single day with only the numbers
/// moved — observed in practice across 5+ consecutive days. Subject matching is grounded in
/// the real provider/model strings from that day's aggregates (not text similarity on
/// LLM-authored prose, which varies too much in wording day to day to compare reliably), so
/// a genuinely new topic of the same type still gets through immediately.
/// </summary>
public static class InsightDeduplicator
{
    // Summary is a deliberate daily digest and BudgetAlert is already uniquely gated
    // per-rule by BudgetAlertService -- neither benefits from suppression here.
    private static readonly InsightType[] SuppressibleTypes =
    [
        InsightType.Anomaly,
        InsightType.Efficiency,
        InsightType.Recommendation,
    ];

    // Trimmed from token edges only: model ids carry internal punctuation ("claude-opus-4-5")
    // that must survive, while markdown emphasis and sentence punctuation around them must not.
    private static readonly char[] TokenEdgePunctuation =
    [
        ',',
        '.',
        ';',
        ':',
        '(',
        ')',
        '[',
        ']',
        '{',
        '}',
        '"',
        '\'',
        '`',
        '*',
        '_',
        '<',
        '>',
        '|',
        '!',
        '?',
        '£',
        '$',
    ];

    public static readonly Duration StalenessWindow = Duration.FromDays(4);

    public static bool ShouldSuppress(
        Insight candidate,
        IReadOnlyCollection<Insight> unacknowledged,
        InsightKnownSubjects knownSubjects,
        Instant now
    )
    {
        if (!SuppressibleTypes.Contains(candidate.InsightType))
        {
            return false;
        }

        var candidateModels = MentionedSubjects(candidate, knownSubjects.Models);
        var candidateProviders = MentionedSubjects(candidate, knownSubjects.Providers);
        if (candidateModels.Count == 0 && candidateProviders.Count == 0)
        {
            return false;
        }

        return unacknowledged.Any(existing =>
            existing.InsightType == candidate.InsightType
            && now - existing.GeneratedAt <= StalenessWindow
            && SameCostBasisStory(candidate, existing)
            && SharesSubject(candidateModels, candidateProviders, existing, knownSubjects)
        );
    }

    private static bool SharesSubject(
        HashSet<string> candidateModels,
        HashSet<string> candidateProviders,
        Insight existing,
        InsightKnownSubjects knownSubjects
    )
    {
        var existingModels = MentionedSubjects(existing, knownSubjects.Models);
        if (candidateModels.Count > 0 || existingModels.Count > 0)
        {
            return candidateModels.Overlaps(existingModels);
        }

        return candidateProviders.Overlaps(MentionedSubjects(existing, knownSubjects.Providers));
    }

    // A notional anomaly and a billed anomaly about the same model are financially distinct
    // stories, so the cost basis is part of the match key. Only a basis declared in the
    // insight's Data JSON counts; when either side declares none, the match falls back to
    // subject alone rather than never suppressing.
    private static bool SameCostBasisStory(Insight candidate, Insight existing)
    {
        var candidateBasis = DeclaredCostBasis(candidate);
        var existingBasis = DeclaredCostBasis(existing);
        return candidateBasis is null
            || existingBasis is null
            || string.Equals(candidateBasis, existingBasis, StringComparison.OrdinalIgnoreCase);
    }

    private static string? DeclaredCostBasis(Insight insight)
    {
        try
        {
            using var data = JsonDocument.Parse(insight.Data);
            // The root must be an object before TryGetProperty: model-authored `data` can be a
            // well-formed non-object ("[]", "5", "\"text\""), and TryGetProperty throws
            // InvalidOperationException — not JsonException — on those, which would escape this
            // catch and abort the whole generation pass.
            return
                data.RootElement.ValueKind == JsonValueKind.Object
                && data.RootElement.TryGetProperty("costBasis", out var value)
                && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static HashSet<string> MentionedSubjects(Insight insight, IReadOnlyCollection<string> subjects)
    {
        // Title and body only: the raw Data JSON would match subjects inside property names and
        // values that never appear on the visible card. Whole tokens, not substrings, so a
        // shorter model id that is a prefix of a longer one ("claude-opus-4" inside
        // "claude-opus-4-5") does not collide.
        var tokens = Tokenize($"{insight.Title} {insight.Body}");
        return subjects.Where(tokens.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> Tokenize(string text)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split(null as char[], StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim(TokenEdgePunctuation);
            if (token.Length > 0)
            {
                tokens.Add(token);
            }
        }

        return tokens;
    }
}
