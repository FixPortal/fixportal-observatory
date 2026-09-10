using AiObservatory.Data;
using AiObservatory.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace AiObservatory.Api.Endpoints;

public static class SourceStatusEndpoints
{
    public static IEndpointRouteBuilder MapSourceStatusEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/sources/status", GetSourceStatusAsync);
        return app;
    }

    public static string Classify(SourceSyncState state, Instant now)
    {
        if (!state.IsConfigured)
        {
            return "notConfigured";
        }
        if (state.IsAvailable == false)
        {
            return "unavailable";
        }
        if (state.ConsecutiveFailureCount > 0)
        {
            return "failing";
        }
        if (state.LastSuccessAt is null)
        {
            return "configured";
        }

        var elapsedNanoseconds = (now - state.LastSuccessAt.Value).ToInt128Nanoseconds();
        var staleAfterNanoseconds =
            (Int128)state.ExpectedRefreshIntervalSeconds * 2 * NodaConstants.NanosecondsPerSecond;
        return elapsedNanoseconds > staleAfterNanoseconds ? "stale" : "fresh";
    }

    private static async Task<IResult> GetSourceStatusAsync(
        AiObservatoryDbContext db,
        IClock clock,
        CancellationToken ct
    )
    {
        var now = clock.GetCurrentInstant();
        var statuses = await db
            .SourceSyncStates.AsNoTracking()
            .OrderBy(state => state.SourceId)
            .Select(state => new SourceStatusResponse(
                state.SourceId,
                Classify(state, now),
                state.IsConfigured,
                state.LastAttemptAt,
                state.LastSuccessAt,
                state.LatestObservationAt,
                state.ConsecutiveFailureCount,
                state.LastError
            ))
            .ToListAsync(ct);

        return Results.Ok(statuses);
    }
}

public sealed record SourceStatusResponse(
    string SourceId,
    string Status,
    bool IsConfigured,
    Instant? LastAttemptAt,
    Instant? LastSuccessAt,
    Instant? LatestObservationAt,
    int ConsecutiveFailureCount,
    string? LastError
);
