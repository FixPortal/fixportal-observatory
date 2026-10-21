using AiObservatory.Data;
using AiObservatory.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace AiObservatory.Api.Endpoints;

// Request records are instantiated by ASP.NET Core model binding.
// ReSharper disable ClassNeverInstantiated.Global

public static class SubscriptionsEndpoints
{
    // Returning the builder is the standard fluent endpoint-mapping convention.
    // ReSharper disable once UnusedMethodReturnValue.Global
    public static IEndpointRouteBuilder MapSubscriptionsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/subscriptions", GetSubscriptionsAsync);
        app.MapGet("/subscriptions/{id:guid}", GetSubscriptionByIdAsync).WithName("GetSubscriptionById");
        app.MapPost("/subscriptions", CreateSubscriptionAsync);
        app.MapPut("/subscriptions/{id:guid}", UpdateSubscriptionAsync);
        app.MapPatch("/subscriptions/{id:guid}/extra-usage", UpdateExtraUsageAsync);
        app.MapDelete("/subscriptions/{id:guid}", DeleteSubscriptionAsync);

        return app;
    }

    private static async Task<IResult> GetSubscriptionsAsync(AiObservatoryDbContext db) =>
        Results.Ok(await db.Subscriptions.AsNoTracking().ToListAsync());

    private static async Task<IResult> GetSubscriptionByIdAsync(Guid id, AiObservatoryDbContext db)
    {
        var sub = await db.Subscriptions.FindAsync(id);
        return sub is not null ? Results.Ok(sub) : Results.NotFound();
    }

    private static async Task<IResult> CreateSubscriptionAsync(
        SubscriptionRequest req,
        AiObservatoryDbContext db,
        CancellationToken ct
    )
    {
        var error = ValidateSubscription(req, out var provider, out var currency, out var billingInterval);
        if (error is not null)
        {
            return error;
        }

        var sub = new Subscription();
        Apply(req, provider, currency, billingInterval, sub);
        db.Subscriptions.Add(sub);
        await db.SaveChangesAsync(ct);
        return Results.CreatedAtRoute("GetSubscriptionById", new { id = sub.Id }, sub);
    }

    private static async Task<IResult> UpdateSubscriptionAsync(
        Guid id,
        SubscriptionRequest req,
        AiObservatoryDbContext db,
        CancellationToken ct
    )
    {
        var error = ValidateSubscription(req, out var provider, out var currency, out var billingInterval);
        if (error is not null)
        {
            return error;
        }

        var sub = await db.Subscriptions.FindAsync([id], ct);
        if (sub is null)
        {
            return Results.NotFound();
        }

        Apply(req, provider, currency, billingInterval, sub);
        await db.SaveChangesAsync(ct);
        return Results.Ok(sub);
    }

    private static async Task<IResult> UpdateExtraUsageAsync(
        Guid id,
        ExtraUsageRequest req,
        AiObservatoryDbContext db,
        CancellationToken ct
    )
    {
        // Reject a negative extra-usage amount: it would deflate the subscription's
        // total spend, its % of budget, and the over-budget flag (frontend guard alone
        // is bypassable — a typed "-5" passes the input's min="0").
        if (req.Amount is < 0)
        {
            return Results.BadRequest("Amount must be non-negative");
        }

        var sub = await db.Subscriptions.FindAsync([id], ct);
        if (sub is null)
        {
            return Results.NotFound();
        }

        sub.ExtraUsageCost = req.Amount;
        await db.SaveChangesAsync(ct);
        return Results.Ok(sub);
    }

    private static async Task<IResult> DeleteSubscriptionAsync(Guid id, AiObservatoryDbContext db)
    {
        await db.Subscriptions.Where(s => s.Id == id).ExecuteDeleteAsync();
        return Results.NoContent();
    }

    private static IResult? ValidateSubscription(
        SubscriptionRequest req,
        out Provider provider,
        out string currency,
        out SubscriptionBillingInterval billingInterval
    )
    {
        if (!Enum.TryParse(req.Provider, ignoreCase: true, out provider) || !Enum.IsDefined(provider))
        {
            currency = "";
            billingInterval = default;
            return Results.BadRequest($"Unknown provider: {req.Provider}");
        }

        if (
            !Enum.TryParse(req.BillingInterval, ignoreCase: true, out billingInterval)
            || !Enum.IsDefined(billingInterval)
        )
        {
            currency = "";
            return Results.BadRequest($"Unknown billing interval: {req.BillingInterval}");
        }

        if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 200)
        {
            currency = "";
            return Results.BadRequest("Name is required and must be 200 characters or fewer");
        }

        if (req.BillingDay < 1 || req.BillingDay > 31)
        {
            currency = "";
            return Results.BadRequest("BillingDay must be between 1 and 31");
        }
        if (billingInterval == SubscriptionBillingInterval.Annual && req.BillingMonth is not (>= 1 and <= 12))
        {
            currency = "";
            return Results.BadRequest("BillingMonth must be between 1 and 12 for annual subscriptions");
        }

        if (req.CostAmount < 0)
        {
            currency = "";
            return Results.BadRequest("CostAmount must be non-negative");
        }

        if (string.IsNullOrWhiteSpace(req.Currency))
        {
            currency = "";
            return Results.BadRequest("Currency must be GBP or USD");
        }

        currency = req.Currency.ToUpperInvariant();
        return currency is not ("GBP" or "USD") ? Results.BadRequest("Currency must be GBP or USD") : null;
    }

    private static void Apply(
        SubscriptionRequest req,
        Provider provider,
        string currency,
        SubscriptionBillingInterval billingInterval,
        Subscription sub
    )
    {
        sub.Provider = provider;
        sub.Name = req.Name;
        sub.CostAmount = req.CostAmount;
        sub.Currency = currency;
        sub.BillingInterval = billingInterval;
        sub.BillingMonth = billingInterval == SubscriptionBillingInterval.Annual ? req.BillingMonth : null;
        sub.BillingDay = req.BillingDay;
        sub.ActiveFrom = req.ActiveFrom;
        sub.ActiveTo = req.ActiveTo;
    }
}

public record SubscriptionRequest(
    string Provider,
    string Name,
    decimal CostAmount,
    string Currency,
    int BillingDay,
    LocalDate ActiveFrom,
    string BillingInterval = "monthly",
    int? BillingMonth = null,
    LocalDate? ActiveTo = null
);

public sealed record ExtraUsageRequest(decimal? Amount);
