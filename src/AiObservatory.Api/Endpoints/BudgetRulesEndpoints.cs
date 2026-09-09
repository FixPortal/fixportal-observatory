using AiObservatory.Data;
using AiObservatory.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace AiObservatory.Api.Endpoints;

// Request records are instantiated by ASP.NET Core model binding.
// ReSharper disable ClassNeverInstantiated.Global

public static class BudgetRulesEndpoints
{
    // Returning the builder is the standard fluent endpoint-mapping convention.
    // ReSharper disable once UnusedMethodReturnValue.Global
    public static IEndpointRouteBuilder MapBudgetRulesEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/budget-rules", GetBudgetRulesAsync);

        app.MapGet(
                "/budget-rules/{id:guid}",
                async (Guid id, AiObservatoryDbContext db, CancellationToken ct) =>
                {
                    var rule = await db.BudgetRules.FindAsync([id], ct);
                    return rule is not null ? Results.Ok(rule) : Results.NotFound();
                }
            )
            .WithName("GetBudgetRuleById");

        app.MapPost(
            "/budget-rules",
            async (CreateBudgetRuleRequest req, AiObservatoryDbContext db, IClock clock, CancellationToken ct) =>
            {
                // A zero/negative threshold is exceeded by any spend, so the rule fires a
                // spurious alert (plus Insight row + email) every period until deleted.
                if (req.ThresholdGbp <= 0)
                {
                    return Results.BadRequest("ThresholdGbp must be greater than zero");
                }

                var rule = new BudgetRule
                {
                    Provider = req.Provider,
                    Period = req.Period,
                    ThresholdGbp = req.ThresholdGbp,
                    EvaluationStartsOn = clock.GetCurrentInstant().InUtc().Date,
                };
                db.BudgetRules.Add(rule);
                await db.SaveChangesAsync(ct);
                return Results.CreatedAtRoute("GetBudgetRuleById", new { id = rule.Id }, rule);
            }
        );

        app.MapDelete(
            "/budget-rules/{id:guid}",
            async (Guid id, AiObservatoryDbContext db, CancellationToken ct) =>
            {
                // Report the row count: a 204 for an id that never existed would let a client
                // believe it deleted something it didn't (mirrors DeleteEntryAsync).
                var deleted = await db.BudgetRules.Where(r => r.Id == id).ExecuteDeleteAsync(ct);
                return deleted == 0 ? Results.NotFound() : Results.NoContent();
            }
        );

        return app;
    }

    private static async Task<IResult> GetBudgetRulesAsync(
        AiObservatoryDbContext db,
        IClock clock,
        CancellationToken ct
    )
    {
        var rules = await db.BudgetRules.AsNoTracking().ToListAsync(ct);
        if (rules.Count == 0)
        {
            return Results.Ok(Array.Empty<BudgetRuleResponse>());
        }

        var today = clock.GetCurrentInstant().InUtc().Date;
        var windows = rules.ToDictionary(rule => rule.Id, rule => CurrentWindow(rule, today));
        var earliest = windows.Values.Min(window => window.Start);
        var spend = await db
            .SpendEntries.AsNoTracking()
            .Where(entry => entry.OccurredOn >= earliest && entry.OccurredOn <= today)
            .Join(
                db.SpendVendors.AsNoTracking(),
                entry => entry.VendorId,
                vendor => vendor.Id,
                (entry, vendor) =>
                    new
                    {
                        entry.OccurredOn,
                        entry.AmountGbp,
                        vendor.Provider,
                    }
            )
            .GroupBy(row => new { row.OccurredOn, row.Provider })
            .Select(group => new
            {
                group.Key.OccurredOn,
                group.Key.Provider,
                AmountGbp = group.Sum(row => row.AmountGbp),
            })
            .ToListAsync(ct);

        return Results.Ok(
            rules.Select(rule =>
            {
                var (windowStart, windowEnd) = windows[rule.Id];
                var currentSpend = spend
                    .Where(row =>
                        row.OccurredOn >= windowStart
                        && row.OccurredOn <= windowEnd
                        && (rule.Provider is null || row.Provider == rule.Provider)
                    )
                    .Sum(row => row.AmountGbp);
                return new BudgetRuleResponse(
                    rule.Id,
                    rule.Provider,
                    rule.Period,
                    rule.ThresholdGbp,
                    rule.EvaluationStartsOn,
                    rule.LastTriggeredAt,
                    currentSpend,
                    windowStart,
                    windowEnd
                );
            })
        );
    }

    private static (LocalDate Start, LocalDate End) CurrentWindow(BudgetRule rule, LocalDate today)
    {
        // Daily must match BudgetAlertService.GetWindow, which evaluates the last COMPLETED
        // day (yesterday): scoping the panel to today's partial spend would show "Over limit"
        // for a day the alert engine won't look at until the next midnight run.
        (LocalDate Start, LocalDate End) nominal = rule.Period switch
        {
            BillingPeriod.Daily => (today.PlusDays(-1), today.PlusDays(-1)),
            BillingPeriod.Weekly => (today.PlusDays(-6), today),
            BillingPeriod.Monthly => (new LocalDate(today.Year, today.Month, 1), today),
            _ => (today, today),
        };
        // Clamp BOTH ends to the evaluation boundary: a Daily rule created today has a nominal
        // window of (yesterday, yesterday), and clamping only Start would return
        // WindowStart (today) > WindowEnd (yesterday) — an inverted window on the rule's first
        // day. Accounting is unaffected either way (the spend filter over an inverted range is
        // simply empty); this keeps the response shape sane for the panel.
        var start = nominal.Start > rule.EvaluationStartsOn ? nominal.Start : rule.EvaluationStartsOn;
        return (start, nominal.End < start ? start : nominal.End);
    }
}

public sealed record CreateBudgetRuleRequest(Provider? Provider, BillingPeriod Period, decimal ThresholdGbp);

public sealed record BudgetRuleResponse(
    Guid Id,
    Provider? Provider,
    BillingPeriod Period,
    decimal ThresholdGbp,
    LocalDate EvaluationStartsOn,
    Instant? LastTriggeredAt,
    decimal CurrentSpendGbp,
    LocalDate WindowStart,
    LocalDate WindowEnd
);
