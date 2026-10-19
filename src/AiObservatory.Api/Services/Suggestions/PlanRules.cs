using System.Globalization;
using AiObservatory.Data.Entities;
using NodaTime;

namespace AiObservatory.Api.Services.Suggestions;

/// <summary>
/// Shared plumbing for the plan rules. A provider is the subject because usage cannot be split between
/// plans; if any active plan of a provider cannot be evaluated, the whole provider is not evaluated.
/// </summary>
public abstract class PlanRuleBase : ISuggestionRule
{
    public abstract string RuleId { get; }

    public abstract RuleOutcome Evaluate(SuggestionInputs inputs, SuggestionOptions options);

    protected static IEnumerable<IGrouping<Provider, Subscription>> ActiveByProvider(SuggestionInputs inputs) =>
        inputs
            .Subscriptions.Where(s =>
                s.ActiveFrom <= inputs.Today && (s.ActiveTo is null || s.ActiveTo >= inputs.Today)
            )
            .GroupBy(s => s.Provider)
            .OrderBy(g => g.Key.ToString(), StringComparer.Ordinal);

    /// <summary>Returns a reason when any active plan of the provider is not usable, otherwise null.</summary>
    protected static string? Unusable(
        IGrouping<Provider, Subscription> plans,
        SuggestionInputs inputs,
        SuggestionOptions options
    )
    {
        foreach (var plan in plans)
        {
            if (
                !plan.Currency.Equals("GBP", StringComparison.OrdinalIgnoreCase)
                && !plan.Currency.Equals("USD", StringComparison.OrdinalIgnoreCase)
            )
            {
                return $"{plans.Key}: a plan is priced in {plan.Currency}, only GBP and USD are supported (currency)";
            }
            if (Period.Between(plan.ActiveFrom, inputs.Today, PeriodUnits.Days).Days < options.PlanMinActiveDays)
            {
                return $"{plans.Key}: a plan has been active under {options.PlanMinActiveDays} days";
            }
        }
        return null;
    }

    protected static string Reasons(IReadOnlyList<string> reasons, string whenEmpty) =>
        reasons.Count == 0 ? whenEmpty : string.Join("; ", reasons.Distinct(StringComparer.Ordinal));

    protected static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}

public sealed class PlanCostsMoreThanUseRule : PlanRuleBase
{
    public override string RuleId => "plan.costs-more-than-use";

    public override RuleOutcome Evaluate(SuggestionInputs inputs, SuggestionOptions options)
    {
        var candidates = new List<SuggestionCandidate>();
        var skipped = new List<string>();
        var judged = 0;
        var any = false;

        foreach (var plans in ActiveByProvider(inputs))
        {
            any = true;
            var unusable = Unusable(plans, inputs, options);
            if (unusable is not null)
            {
                skipped.Add(unusable);
                continue;
            }
            // Notional usage is in USD, so every comparison needs a live USD to GBP rate.
            if (inputs.UsdToGbp is not { } rate)
            {
                skipped.Add("FX unavailable");
                continue;
            }

            var notional = inputs.Aggregates.Where(a =>
                a.Provider == plans.Key
                && a.CostBasis == CostBasis.Notional
                && a.Date >= inputs.PlanWindow.From
                && a.Date <= inputs.PlanWindow.To
            );
            var knownRequests = notional.Sum(a => Math.Max(0, a.RequestCount - a.UnknownCostCount));
            if (knownRequests < options.PlanMinKnownCostRequests)
            {
                skipped.Add(
                    $"{plans.Key}: fewer than {options.PlanMinKnownCostRequests} known-cost requests in the last {options.PlanWindowDays} days"
                );
                continue;
            }

            judged++;
            var usageGbp = notional.Where(a => a.RequestCount > a.UnknownCostCount).Sum(a => a.CostUsd) * rate;
            var priceGbp = plans.Sum(p => MonthlyGbp(p, rate));
            if (priceGbp <= 0 || (double)(usageGbp / priceGbp) >= options.PlanUsageRatio)
            {
                continue;
            }

            var ratio = Math.Round(usageGbp / priceGbp, 2);
            candidates.Add(
                new SuggestionCandidate(
                    RuleId,
                    plans.Key.ToString(),
                    "attention",
                    $"{plans.Key} plans cost more than the usage they covered ({ratio.ToString("0.##", CultureInfo.InvariantCulture)} of the monthly price)",
                    new SuggestionEvidence(
                        inputs.PlanWindow,
                        knownRequests,
                        [
                            new SuggestionFigure("Notional usage value", Math.Round(usageGbp, 2), "GBP"),
                            new SuggestionFigure("Monthly plan price", Math.Round(priceGbp, 2), "GBP"),
                            new SuggestionFigure("Usage to price ratio", ratio, "ratio"),
                            new SuggestionFigure("fxRate", rate, "GBP per USD"),
                            new SuggestionFigure("fxSource", "live", "text"),
                        ],
                        []
                    ),
                    "Usage to price ratio"
                )
            );
        }

        if (judged == 0)
        {
            return RuleOutcome.NotEvaluated(
                RuleId,
                Reasons(skipped, any ? "No provider could be judged" : "No active subscriptions")
            );
        }
        return new RuleOutcome(RuleId, candidates, true, null, judged);
    }

    // Annual prices are spread over twelve months; USD prices use the same live rate as the usage.
    private static decimal MonthlyGbp(Subscription plan, decimal usdToGbp)
    {
        var gbp = plan.Currency.Equals("USD", StringComparison.OrdinalIgnoreCase)
            ? plan.CostAmount * usdToGbp
            : plan.CostAmount;
        return plan.BillingInterval == SubscriptionBillingInterval.Annual ? gbp / 12m : gbp;
    }
}

public sealed class PlanUnusedRule : PlanRuleBase
{
    public override string RuleId => "plan.unused";

    public override RuleOutcome Evaluate(SuggestionInputs inputs, SuggestionOptions options)
    {
        var candidates = new List<SuggestionCandidate>();
        var skipped = new List<string>();
        var judged = 0;

        foreach (var plans in ActiveByProvider(inputs))
        {
            var unusable = Unusable(plans, inputs, options);
            if (unusable is not null)
            {
                skipped.Add(unusable);
                continue;
            }

            judged++;
            var requests = inputs
                .Aggregates.Where(a =>
                    a.Provider == plans.Key && a.Date >= inputs.PlanWindow.From && a.Date <= inputs.PlanWindow.To
                )
                .Sum(a => a.RequestCount);
            if (requests > 0)
            {
                continue;
            }

            candidates.Add(
                new SuggestionCandidate(
                    RuleId,
                    plans.Key.ToString(),
                    "attention",
                    $"No {plans.Key} usage in the last {options.PlanWindowDays} days despite {Plural(plans.Count(), "active plan")}",
                    new SuggestionEvidence(
                        inputs.PlanWindow,
                        0,
                        [
                            new SuggestionFigure("Requests in window", 0, "requests"),
                            new SuggestionFigure("Active plans", plans.Count(), "plans"),
                        ],
                        []
                    ),
                    "Requests in window"
                )
            );
        }

        if (judged == 0)
        {
            return RuleOutcome.NotEvaluated(RuleId, Reasons(skipped, "No active subscriptions"));
        }
        return new RuleOutcome(RuleId, candidates, true, null, judged);
    }
}
