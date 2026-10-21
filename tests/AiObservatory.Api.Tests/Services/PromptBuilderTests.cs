using AiObservatory.Api.Services.Intelligence;
using AiObservatory.Data.Entities;
using AwesomeAssertions;
using NodaTime;

namespace AiObservatory.Api.Tests.Services;

public class PromptBuilderTests
{
    [Fact]
    public void Build_includes_total_spend_for_period()
    {
        var aggregates = new List<DailyAggregate>
        {
            new()
            {
                Date = new LocalDate(2026, 6, 1),
                Provider = Provider.Anthropic,
                Model = "claude-opus-4-8",
                InputTokens = 10_000,
                OutputTokens = 2_000,
                CacheReadTokens = 1_234,
                CacheWriteTokens = 56,
                CostUsd = 5.00m,
                RequestCount = 10,
            },
            new()
            {
                Date = new LocalDate(2026, 6, 1),
                Provider = Provider.Anthropic,
                Model = "claude-sonnet-4-6",
                InputTokens = 50_000,
                OutputTokens = 8_000,
                CostUsd = 2.50m,
                RequestCount = 40,
            },
        };
        var subscriptions = new List<Subscription>
        {
            new()
            {
                Provider = Provider.Copilot,
                Name = "GitHub Copilot",
                CostAmount = 9.40m,
                Currency = "GBP",
                BillingDay = 1,
            },
        };

        var sut = new PromptBuilder();
        // USD-native $7.50 total at a 0.80 USD->GBP rate => £6.00.
        var prompt = sut.Build(aggregates, subscriptions, new LocalDate(2026, 6, 1), new LocalDate(2026, 6, 1), 0.80m);

        prompt.Should().Contain("£6.00"); // total API spend, converted to GBP
        prompt.Should().NotContain("$"); // never report in dollars
        prompt.Should().Contain("claude-opus-4-8");
        prompt.Should().Contain(", Cache: 1234 read, 56 write");
    }

    [Fact]
    public void Build_includes_cache_hint_for_anthropic_data()
    {
        var aggregates = new List<DailyAggregate>
        {
            new()
            {
                Date = new LocalDate(2026, 6, 1),
                Provider = Provider.Anthropic,
                Model = "claude-opus-4-8",
                InputTokens = 10_000,
                OutputTokens = 2_000,
                CostUsd = 5.00m,
                RequestCount = 5,
            },
        };

        var sut = new PromptBuilder();
        var prompt = sut.Build(aggregates, [], new LocalDate(2026, 6, 1), new LocalDate(2026, 6, 1), 1m);

        prompt.Should().Contain("cache");
    }

    [Fact]
    public void Build_normalises_annual_subscription_in_monthly_total()
    {
        var subscriptions = new List<Subscription>
        {
            new()
            {
                Provider = Provider.OpenAI,
                Name = "Monthly plan",
                CostAmount = 200m,
                Currency = "GBP",
                BillingInterval = SubscriptionBillingInterval.Monthly,
                BillingDay = 8,
            },
            new()
            {
                Provider = Provider.Google,
                Name = "Google One",
                CostAmount = 189.99m,
                Currency = "GBP",
                BillingInterval = SubscriptionBillingInterval.Annual,
                BillingMonth = 7,
                BillingDay = 2,
            },
        };

        var sut = new PromptBuilder();
        var prompt = sut.Build([], subscriptions, new LocalDate(2026, 8, 1), new LocalDate(2026, 8, 27), 1m);

        prompt.Should().Contain("GBP 189.99/year (~GBP 0.52/day)");
        prompt
            .Should()
            .Contain("Equivalent flat-rate subscription total (annual plans divided by 12): GBP 215.83/month");
    }

    [Fact]
    public void Build_does_not_describe_unpriced_usage_as_zero_spend()
    {
        var aggregates = new List<DailyAggregate>
        {
            new()
            {
                Date = new LocalDate(2026, 8, 27),
                Provider = Provider.Anthropic,
                Model = "claude-opus-4-1",
                CostBasis = CostBasis.Notional,
                CostUsd = 0,
                RequestCount = 42,
                UnknownCostCount = 42,
            },
        };

        var prompt = new PromptBuilder().Build(
            aggregates,
            [],
            new LocalDate(2026, 8, 1),
            new LocalDate(2026, 8, 27),
            1m
        );

        prompt.Should().Contain("Anthropic: Not reported (42 requests)");
        prompt.Should().Contain("claude-opus-4-1: Not reported, 42 requests");
        prompt.Should().NotContain("Anthropic: £0.00");
        prompt.Should().NotContain("Total API spend");
        prompt.Should().Contain("[NOTIONAL]");
        prompt.Should().Contain("never as \"spend\", \"cost\", \"billed\", or \"API cost\" on its own");
    }

    [Fact]
    public void Build_tags_every_provider_and_model_figure_with_its_cost_basis()
    {
        // Regression: a provider/model breakdown line with no cost-basis tag let the
        // generated narrative call fully subscription-covered (never billed) usage
        // "spend" / "API cost" -- verified live on prod, where every aggregate row for
        // the period was CostBasis.Notional yet insights repeatedly said "billed API
        // spend". Every reported figure must carry its own basis tag so the model can't
        // misattribute one after the fact.
        var aggregates = new List<DailyAggregate>
        {
            new()
            {
                Date = new LocalDate(2026, 8, 1),
                Provider = Provider.OpenAI,
                Model = "gpt-5.6-sol",
                CostBasis = CostBasis.Notional,
                CostUsd = 900.15m,
                RequestCount = 1,
            },
            new()
            {
                Date = new LocalDate(2026, 8, 2),
                Provider = Provider.OpenAI,
                Model = "gpt-5.6-terra",
                CostBasis = CostBasis.Billed,
                CostUsd = 12.50m,
                RequestCount = 3,
            },
        };

        var prompt = new PromptBuilder().Build(
            aggregates,
            [],
            new LocalDate(2026, 8, 1),
            new LocalDate(2026, 8, 2),
            1m
        );

        prompt.Should().Contain("gpt-5.6-sol: £900.15 [NOTIONAL]");
        prompt.Should().Contain("gpt-5.6-terra: £12.50 [BILLED]");
        // Same provider, split across two cost-basis lines rather than one merged figure.
        prompt.Should().Contain("OpenAI: £900.15 [NOTIONAL]");
        prompt.Should().Contain("OpenAI: £12.50 [BILLED]");
    }

    [Fact]
    public void Build_emits_the_topline_total_per_cost_basis_never_as_an_untagged_mixed_sum()
    {
        // Regression (M3): the headline total summed Billed and Notional rows into one bare
        // figure two paragraphs before the prompt told the model never to sum across bases —
        // so the model quoted subscription-covered notional usage as actual expenditure.
        var aggregates = new List<DailyAggregate>
        {
            new()
            {
                Date = new LocalDate(2026, 8, 1),
                Provider = Provider.OpenAI,
                Model = "gpt-5.6-sol",
                CostBasis = CostBasis.Notional,
                CostUsd = 900.15m,
                RequestCount = 1,
            },
            new()
            {
                Date = new LocalDate(2026, 8, 2),
                Provider = Provider.OpenAI,
                Model = "gpt-5.6-terra",
                CostBasis = CostBasis.Billed,
                CostUsd = 12.50m,
                RequestCount = 3,
            },
        };

        var prompt = new PromptBuilder().Build(
            aggregates,
            [],
            new LocalDate(2026, 8, 1),
            new LocalDate(2026, 8, 2),
            1m
        );

        prompt.Should().Contain("Total reported usage value by cost basis:");
        prompt.Should().Contain("£12.50 [BILLED]");
        prompt.Should().Contain("£900.15 [NOTIONAL]");
        prompt
            .Should()
            .NotContain(
                "Total reported usage value: £912.65",
                "a cross-basis sum must never appear as one untagged figure"
            );
    }

    [Fact]
    public void Build_tags_the_yesterday_comparison_and_suppresses_it_for_a_mixed_basis_period()
    {
        // The yesterday-vs-average line sums across the whole window, so it is only emitted
        // when the window is basis-pure — and then carries the same tag as every other figure.
        static List<DailyAggregate> Aggregates(CostBasis firstBasis, CostBasis secondBasis) =>
            [
                new()
                {
                    Date = new LocalDate(2026, 8, 1),
                    Provider = Provider.Anthropic,
                    Model = "claude-opus-4-8",
                    CostBasis = firstBasis,
                    CostUsd = 10m,
                    RequestCount = 5,
                },
                new()
                {
                    Date = new LocalDate(2026, 8, 2),
                    Provider = Provider.Anthropic,
                    Model = "claude-opus-4-8",
                    CostBasis = secondBasis,
                    CostUsd = 20m,
                    RequestCount = 5,
                },
            ];

        var pure = new PromptBuilder().Build(
            Aggregates(CostBasis.Billed, CostBasis.Billed),
            [],
            new LocalDate(2026, 8, 1),
            new LocalDate(2026, 8, 2),
            1m
        );
        var mixed = new PromptBuilder().Build(
            Aggregates(CostBasis.Billed, CostBasis.Notional),
            [],
            new LocalDate(2026, 8, 1),
            new LocalDate(2026, 8, 2),
            1m
        );

        pure.Should().Contain("Yesterday reported usage value: £20.00 [BILLED] vs 30-day average: £10.00/day [BILLED]");
        mixed
            .Should()
            .NotContain(
                "Yesterday reported usage value",
                "summing yesterday across mixed bases would reproduce the untagged-total problem"
            );
    }
}
