using AiObservatory.Api.Services.Intelligence;
using AiObservatory.Data.Entities;
using AwesomeAssertions;
using NodaTime;

namespace AiObservatory.Api.Tests.Services;

public class InsightDeduplicatorTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 8, 30, 0, 0);
    private static readonly InsightKnownSubjects Subjects = new(
        ["gpt-5.6-sol", "claude-opus-5"],
        ["OpenAI", "Anthropic"]
    );

    private static Insight Insight(
        InsightType type,
        string title,
        string body = "",
        Instant? generatedAt = null,
        string data = "{}"
    ) =>
        new()
        {
            InsightType = type,
            Title = title,
            Body = body,
            Data = data,
            GeneratedAt = generatedAt ?? Now,
        };

    [Fact]
    public void Suppresses_a_same_type_same_subject_repeat_within_the_staleness_window()
    {
        var existing = Insight(
            InsightType.Anomaly,
            "gpt-5.6-sol: Disproportionate Cost Per Request",
            generatedAt: Now - Duration.FromDays(1)
        );
        var candidate = Insight(InsightType.Anomaly, "gpt-5.6-sol Dominates Costs at 97% of API Spend");

        InsightDeduplicator.ShouldSuppress(candidate, [existing], Subjects, Now).Should().BeTrue();
    }

    [Fact]
    public void Does_not_suppress_a_different_subject_of_the_same_type()
    {
        var existing = Insight(
            InsightType.Anomaly,
            "gpt-5.6-sol: Disproportionate Cost Per Request",
            generatedAt: Now - Duration.FromDays(1)
        );
        // A genuinely new anomaly about a different model must not be blocked just
        // because an unrelated anomaly of the same type is still unacknowledged.
        var candidate = Insight(InsightType.Anomaly, "claude-opus-5 cache write volume dropped sharply");

        InsightDeduplicator.ShouldSuppress(candidate, [existing], Subjects, Now).Should().BeFalse();
    }

    [Fact]
    public void Does_not_suppress_two_anomalies_that_share_only_a_provider_name()
    {
        // Provider names appear in nearly every LLM-authored insight; matching on them alone
        // would let one Anthropic anomaly suppress every other Anthropic anomaly for days.
        var existing = Insight(
            InsightType.Anomaly,
            "Anthropic claude-opus-5 spend spiked",
            generatedAt: Now - Duration.FromDays(1)
        );
        var candidate = Insight(InsightType.Anomaly, "Anthropic cache hit rate collapsed");

        InsightDeduplicator.ShouldSuppress(candidate, [existing], Subjects, Now).Should().BeFalse();
    }

    [Fact]
    public void Suppresses_a_provider_only_repeat_when_neither_insight_names_a_model()
    {
        var existing = Insight(
            InsightType.Recommendation,
            "Review Anthropic plan limits",
            generatedAt: Now - Duration.FromDays(1)
        );
        var candidate = Insight(InsightType.Recommendation, "Anthropic subscription needs a look");

        InsightDeduplicator.ShouldSuppress(candidate, [existing], Subjects, Now).Should().BeTrue();
    }

    [Fact]
    public void Does_not_match_a_model_id_that_is_only_a_prefix_of_a_longer_one()
    {
        var subjects = new InsightKnownSubjects(["claude-opus-4", "claude-opus-4-5"], ["Anthropic"]);
        var existing = Insight(
            InsightType.Anomaly,
            "claude-opus-4-5 latency anomaly",
            generatedAt: Now - Duration.FromDays(1)
        );
        // Substring matching would find "claude-opus-4" inside the existing title and suppress.
        var candidate = Insight(InsightType.Anomaly, "claude-opus-4 context errors rising");

        InsightDeduplicator.ShouldSuppress(candidate, [existing], subjects, Now).Should().BeFalse();
    }

    [Fact]
    public void Ignores_subjects_that_appear_only_inside_the_raw_data_json()
    {
        var existing = Insight(
            InsightType.Anomaly,
            "gpt-5.6-sol: Disproportionate Cost Per Request",
            generatedAt: Now - Duration.FromDays(1)
        );
        var candidate = Insight(
            InsightType.Anomaly,
            "Something odd happened yesterday",
            data: """{"relatedModel": "gpt-5.6-sol"}"""
        );

        InsightDeduplicator.ShouldSuppress(candidate, [existing], Subjects, Now).Should().BeFalse();
    }

    [Fact]
    public void Does_not_suppress_across_different_cost_bases_for_the_same_model()
    {
        // A notional anomaly and a billed anomaly about the same model are distinct stories.
        var existing = Insight(
            InsightType.Anomaly,
            "gpt-5.6-sol billed spend spiked",
            generatedAt: Now - Duration.FromDays(1),
            data: """{"costBasis": "billed"}"""
        );
        var candidate = Insight(
            InsightType.Anomaly,
            "gpt-5.6-sol notional usage spiked",
            data: """{"costBasis": "notional"}"""
        );

        InsightDeduplicator.ShouldSuppress(candidate, [existing], Subjects, Now).Should().BeFalse();
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("5")]
    [InlineData("\"text\"")]
    public void A_non_object_data_document_falls_back_to_subject_matching_instead_of_throwing(string data)
    {
        // M20: model-authored `data` can be any well-formed JSON, and TryGetProperty on a
        // non-object root throws InvalidOperationException — not the JsonException the guard
        // caught — aborting the whole generation pass once any same-type insight was
        // unacknowledged. A non-object root declares no cost basis, so subject decides.
        var existing = Insight(
            InsightType.Anomaly,
            "gpt-5.6-sol billed spend spiked",
            generatedAt: Now - Duration.FromDays(1),
            data: data
        );
        var candidate = Insight(InsightType.Anomaly, "gpt-5.6-sol billed spend still elevated", data: data);

        InsightDeduplicator.ShouldSuppress(candidate, [existing], Subjects, Now).Should().BeTrue();
    }

    [Fact]
    public void Suppresses_a_repeat_on_the_same_cost_basis()
    {
        var existing = Insight(
            InsightType.Anomaly,
            "gpt-5.6-sol billed spend spiked",
            generatedAt: Now - Duration.FromDays(1),
            data: """{"costBasis": "billed"}"""
        );
        var candidate = Insight(
            InsightType.Anomaly,
            "gpt-5.6-sol billed spend still elevated",
            data: """{"costBasis": "billed"}"""
        );

        InsightDeduplicator.ShouldSuppress(candidate, [existing], Subjects, Now).Should().BeTrue();
    }

    [Fact]
    public void Does_not_suppress_a_different_insight_type_about_the_same_subject()
    {
        var existing = Insight(
            InsightType.Anomaly,
            "gpt-5.6-sol: Disproportionate Cost Per Request",
            generatedAt: Now - Duration.FromDays(1)
        );
        var candidate = Insight(InsightType.Recommendation, "Route gpt-5.6-sol workloads to a cheaper tier");

        InsightDeduplicator.ShouldSuppress(candidate, [existing], Subjects, Now).Should().BeFalse();
    }

    [Fact]
    public void Does_not_suppress_once_the_existing_insight_is_outside_the_staleness_window()
    {
        var existing = Insight(
            InsightType.Anomaly,
            "gpt-5.6-sol: Disproportionate Cost Per Request",
            generatedAt: Now - InsightDeduplicator.StalenessWindow - Duration.FromDays(1)
        );
        var candidate = Insight(InsightType.Anomaly, "gpt-5.6-sol Dominates Costs at 97% of API Spend");

        InsightDeduplicator.ShouldSuppress(candidate, [existing], Subjects, Now).Should().BeFalse();
    }

    [Theory]
    [InlineData(InsightType.Summary)]
    [InlineData(InsightType.BudgetAlert)]
    public void Never_suppresses_periodic_or_event_triggered_types(InsightType type)
    {
        var existing = Insight(type, "gpt-5.6-sol summary", generatedAt: Now - Duration.FromHours(1));
        var candidate = Insight(type, "gpt-5.6-sol summary, restated");

        InsightDeduplicator.ShouldSuppress(candidate, [existing], Subjects, Now).Should().BeFalse();
    }

    [Fact]
    public void Never_suppresses_when_no_known_subject_is_mentioned_in_the_candidate()
    {
        // Can't confirm the topic matches, so the safe default is to let it through
        // rather than risk hiding a genuinely new story.
        var existing = Insight(
            InsightType.Anomaly,
            "gpt-5.6-sol: Disproportionate Cost Per Request",
            generatedAt: Now - Duration.FromDays(1)
        );
        var candidate = Insight(InsightType.Anomaly, "Something odd happened yesterday");

        InsightDeduplicator.ShouldSuppress(candidate, [existing], Subjects, Now).Should().BeFalse();
    }

    [Fact]
    public void Ignores_an_acknowledged_or_unrelated_insight_list_entirely()
    {
        InsightDeduplicator
            .ShouldSuppress(Insight(InsightType.Anomaly, "gpt-5.6-sol anomaly"), [], Subjects, Now)
            .Should()
            .BeFalse();
    }
}
