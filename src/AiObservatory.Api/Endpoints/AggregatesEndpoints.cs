using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Text;

namespace AiObservatory.Api.Endpoints;

public static class AggregatesEndpoints
{
    // Returning the builder is the standard fluent endpoint-mapping convention.
    // ReSharper disable once UnusedMethodReturnValue.Global
    public static IEndpointRouteBuilder MapAggregatesEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/aggregates", GetAggregatesAsync);

        // Provider-scoped reset. Deletes both raw events and the additive daily
        // aggregates for one provider so a clean backfill can follow. Admin-key
        // gated by ApiKeyEndpointFilter (it's a DELETE). Irreversible.
        app.MapDelete("/aggregates", PurgeAggregatesAsync);

        return app;
    }

    private static async Task<IResult> GetAggregatesAsync(
        AiObservatoryDbContext db,
        IClock clock,
        string? from,
        string? to,
        CancellationToken ct
    )
    {
        var today = clock.GetCurrentInstant().InUtc().Date;
        // Shared with the /activity endpoints so every date-ranged dashboard query defaults
        // to the same thirty-calendar-day window and rejects malformed dates identically.
        if (!ActivityEndpoints.TryParseDateRange(from, to, today, out var start, out var end, out var error))
        {
            return error!;
        }

        var data = await db
            .DailyAggregates.AsNoTracking()
            .Where(a => a.Date >= start && a.Date <= end)
            .OrderBy(a => a.Date)
            .ThenBy(a => a.Provider)
            .ThenBy(a => a.Model)
            .Select(a => new
            {
                // Explicit ISO yyyy-MM-dd. LocalDate.ToString() with no pattern uses the
                // server culture's long-date format ("29 May 2026"), which broke the
                // frontend's slice/sort (it assumes ISO) and scrambled the chart axis.
                date = LocalDatePattern.Iso.Format(a.Date),
                provider = a.Provider,
                a.Model,
                a.SourceId,
                a.SourceKind,
                a.UsageScope,
                a.CostBasis,
                a.InputTokens,
                a.OutputTokens,
                a.CacheReadTokens,
                a.CacheWriteTokens,
                // The one-hour SUBSET of CacheWriteTokens, not an additional total — do not
                // add the two. Exposed because it is the only way to confirm from outside the
                // database that a producer is sending the TTL split at all; without it, a
                // producer silently reverting to all-five-minute looks identical to a correct
                // one on every other field, while understating cost by ~60% on this line.
                a.CacheWrite1hTokens,
                a.CostUsd,
                a.UnknownCostCount,
                a.CacheSavingsUsd,
                a.UnknownCacheSavingsCount,
                a.RequestCount,
            })
            .ToListAsync(ct);

        return Results.Ok(data);
    }

    private static async Task<IResult> PurgeAggregatesAsync(
        string? provider,
        IUsageRepository repo,
        CancellationToken ct
    )
    {
        if (
            string.IsNullOrWhiteSpace(provider)
            || !Enum.TryParse<Provider>(provider, ignoreCase: true, out var parsed)
            || !Enum.IsDefined(parsed)
        )
        {
            return Results.BadRequest("provider query parameter is required (anthropic|google|openai|copilot)");
        }

        var result = await repo.PurgeProviderAsync(parsed, ct);
        return Results.Ok(
            new
            {
                provider = parsed.ToString(),
                deletedEvents = result.DeletedEvents,
                deletedAggregates = result.DeletedAggregates,
            }
        );
    }
}
