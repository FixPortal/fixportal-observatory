using AiObservatory.Api.Services.Suggestions;
using AiObservatory.Data.Entities;
using AwesomeAssertions;
using NodaTime;

namespace AiObservatory.Api.Tests.Suggestions;

public class PlanRulesTests
{
    private static readonly SuggestionOptions Options = new();
    private static readonly LocalDate Today = new(2026, 10, 19);

    private static Subscription Sub(
        Provider provider,
        decimal amount,
        string currency = "GBP",
        SubscriptionBillingInterval interval = SubscriptionBillingInterval.Monthly,
        LocalDate? activeFrom = null,
        LocalDate? activeTo = null
    ) =>
        new()
        {
            Provider = provider,
            Name = $"{provider} plan",
            CostAmount = amount,
            Currency = currency,
            BillingInterval = interval,
            BillingMonth = interval == SubscriptionBillingInterval.Annual ? 3 : null,
            BillingDay = 1,
            ActiveFrom = activeFrom ?? new LocalDate(2026, 1, 1),
            ActiveTo = activeTo,
        };

    private static DailyAggregate Agg(
        Provider provider,
        decimal costUsd,
        int requests,
        int unknown = 0,
        CostBasis basis = CostBasis.Notional,
        LocalDate? date = null
    ) =>
        new()
        {
            Date = date ?? new LocalDate(2026, 10, 10),
            Provider = provider,
            Model = "m",
            CostBasis = basis,
            CostUsd = costUsd,
            RequestCount = requests,
            UnknownCostCount = unknown,
        };

    private static SuggestionInputs Inputs(
        IEnumerable<Subscription> subs,
        IEnumerable<DailyAggregate> aggregates,
        decimal? usdToGbp = 0.79m
    ) =>
        new(
            new DateWindow(new LocalDate(2026, 8, 20), new LocalDate(2026, 10, 18)),
            new DateWindow(new LocalDate(2026, 9, 19), new LocalDate(2026, 10, 18)),
            Today,
            [],
            [],
            [.. subs],
            [.. aggregates],
            usdToGbp
        );

    [Fact]
    public void CostsMoreThanUse_fires_when_usage_is_below_half_the_monthly_price()
    {
        // $10 of notional usage is 7.90 GBP against a 20 GBP plan: ratio 0.395.
        var inputs = Inputs([Sub(Provider.OpenAI, 20m)], [Agg(Provider.OpenAI, 10m, 20)]);

        var outcome = new PlanCostsMoreThanUseRule().Evaluate(inputs, Options);

        outcome.Evaluated.Should().BeTrue();
        var candidate = outcome.Candidates.Should().ContainSingle().Subject;
        candidate.Subject.Should().Be("OpenAI");
        candidate.RuleId.Should().Be("plan.costs-more-than-use");
        candidate.Evidence.SampleSize.Should().Be(20);
        candidate.Evidence.Figures.Should().Contain(f => f.Label == "fxRate" && (decimal)f.Value == 0.79m);
        candidate.Evidence.Figures.Should().Contain(f => f.Label == "fxSource" && (string)f.Value == "live");
        candidate.Evidence.RunIds.Should().BeEmpty();
    }

    [Fact]
    public void CostsMoreThanUse_does_not_fire_when_usage_covers_half_the_price()
    {
        // $20 is 15.80 GBP against 20: ratio 0.79.
        var inputs = Inputs([Sub(Provider.OpenAI, 20m)], [Agg(Provider.OpenAI, 20m, 20)]);

        var outcome = new PlanCostsMoreThanUseRule().Evaluate(inputs, Options);

        outcome.Evaluated.Should().BeTrue();
        outcome.Candidates.Should().BeEmpty();
    }

    [Fact]
    public void CostsMoreThanUse_scales_the_window_total_to_a_month_before_comparing_with_the_price()
    {
        // $20 over a 60-day window is $10 a month: 7.90 GBP against 20, below half. Unscaled it would be 15.80.
        var inputs = Inputs([Sub(Provider.OpenAI, 20m)], [Agg(Provider.OpenAI, 20m, 20)]);

        var outcome = new PlanCostsMoreThanUseRule().Evaluate(inputs, new SuggestionOptions { PlanWindowDays = 60 });

        var candidate = outcome.Candidates.Should().ContainSingle().Subject;
        candidate.Evidence.Figures.Should().Contain(f => f.Label == "Notional usage value" && (decimal)f.Value == 7.9m);
    }

    [Fact]
    public void CostsMoreThanUse_prices_an_annual_plan_per_month_and_sums_plans_on_one_provider()
    {
        // 240 a year is 20 a month; plus a 10 monthly plan makes 30. $15 is 11.85 GBP: below half of 30.
        var inputs = Inputs(
            [Sub(Provider.OpenAI, 240m, interval: SubscriptionBillingInterval.Annual), Sub(Provider.OpenAI, 10m)],
            [Agg(Provider.OpenAI, 15m, 20)]
        );

        var candidate = new PlanCostsMoreThanUseRule()
            .Evaluate(inputs, Options)
            .Candidates.Should()
            .ContainSingle()
            .Subject;

        candidate.Evidence.Figures.Should().Contain(f => f.Label == "Monthly plan price" && (decimal)f.Value == 30m);
    }

    [Fact]
    public void CostsMoreThanUse_converts_a_usd_plan_with_the_rate()
    {
        // A 50 USD plan is 39.50 GBP; $10 of usage is 7.90 GBP, below half.
        var inputs = Inputs([Sub(Provider.OpenAI, 50m, "USD")], [Agg(Provider.OpenAI, 10m, 20)]);

        var candidate = new PlanCostsMoreThanUseRule()
            .Evaluate(inputs, Options)
            .Candidates.Should()
            .ContainSingle()
            .Subject;

        candidate.Evidence.Figures.Should().Contain(f => f.Label == "Monthly plan price" && (decimal)f.Value == 39.5m);
    }

    [Fact]
    public void CostsMoreThanUse_with_an_unsupported_currency_makes_the_provider_not_evaluated()
    {
        var inputs = Inputs(
            [Sub(Provider.OpenAI, 20m), Sub(Provider.OpenAI, 10m, "EUR")],
            [Agg(Provider.OpenAI, 1m, 20)]
        );

        var outcome = new PlanCostsMoreThanUseRule().Evaluate(inputs, Options);

        outcome.Evaluated.Should().BeFalse();
        outcome.Candidates.Should().BeEmpty();
        outcome.Reason.Should().Contain("currency");
    }

    [Fact]
    public void CostsMoreThanUse_with_a_plan_active_under_the_grace_days_is_not_evaluated()
    {
        var inputs = Inputs(
            [Sub(Provider.OpenAI, 20m, activeFrom: Today.PlusDays(-10))],
            [Agg(Provider.OpenAI, 1m, 20)]
        );

        var outcome = new PlanCostsMoreThanUseRule().Evaluate(inputs, Options);

        outcome.Evaluated.Should().BeFalse();
        outcome.Reason.Should().Contain("14");
    }

    [Fact]
    public void CostsMoreThanUse_without_a_live_fx_rate_is_not_evaluated()
    {
        var inputs = Inputs([Sub(Provider.OpenAI, 20m)], [Agg(Provider.OpenAI, 1m, 20)], usdToGbp: null);

        var outcome = new PlanCostsMoreThanUseRule().Evaluate(inputs, Options);

        outcome.Evaluated.Should().BeFalse();
        outcome.Reason.Should().Be("FX unavailable");
    }

    [Fact]
    public void CostsMoreThanUse_below_the_known_cost_request_floor_is_not_evaluated()
    {
        var inputs = Inputs([Sub(Provider.OpenAI, 20m)], [Agg(Provider.OpenAI, 1m, 9)]);

        new PlanCostsMoreThanUseRule().Evaluate(inputs, Options).Evaluated.Should().BeFalse();
    }

    [Fact]
    public void CostsMoreThanUse_ignores_requests_with_unknown_cost_and_rows_outside_the_window()
    {
        // 12 requests, 5 of them unknown: only 7 are known-cost, under the floor of 10. The extra
        // row is outside the window and must not top the count up.
        var inputs = Inputs(
            [Sub(Provider.OpenAI, 20m)],
            [Agg(Provider.OpenAI, 1m, 12, unknown: 5), Agg(Provider.OpenAI, 1m, 50, date: new LocalDate(2026, 8, 1))]
        );

        new PlanCostsMoreThanUseRule().Evaluate(inputs, Options).Evaluated.Should().BeFalse();
    }

    [Fact]
    public void Unused_fires_when_the_provider_has_no_requests_in_the_window()
    {
        var inputs = Inputs(
            [Sub(Provider.Google, 20m)],
            [Agg(Provider.Google, 5m, 3, date: new LocalDate(2026, 8, 1)), Agg(Provider.OpenAI, 5m, 30)]
        );

        var outcome = new PlanUnusedRule().Evaluate(inputs, Options);

        outcome.Evaluated.Should().BeTrue();
        outcome.Candidates.Should().ContainSingle().Which.Subject.Should().Be("Google");
    }

    [Fact]
    public void Unused_counts_requests_of_any_basis()
    {
        var inputs = Inputs([Sub(Provider.Google, 20m)], [Agg(Provider.Google, 0m, 1, basis: CostBasis.Billed)]);

        var outcome = new PlanUnusedRule().Evaluate(inputs, Options);

        outcome.Evaluated.Should().BeTrue();
        outcome.Candidates.Should().BeEmpty();
    }

    [Fact]
    public void Unused_waits_out_the_grace_period_and_needs_no_fx_rate()
    {
        var young = Inputs([Sub(Provider.Google, 20m, activeFrom: Today.PlusDays(-5))], []);
        new PlanUnusedRule().Evaluate(young, Options).Evaluated.Should().BeFalse();

        var noFx = Inputs([Sub(Provider.Google, 20m)], [], usdToGbp: null);
        new PlanUnusedRule().Evaluate(noFx, Options).Candidates.Should().ContainSingle();
    }

    [Fact]
    public void Plan_rules_ignore_subscriptions_that_are_no_longer_active()
    {
        var ended = Inputs([Sub(Provider.Google, 20m, activeTo: Today.PlusDays(-1))], []);

        var outcome = new PlanUnusedRule().Evaluate(ended, Options);

        outcome.Evaluated.Should().BeFalse();
        outcome.Reason.Should().Contain("active");
    }
}
