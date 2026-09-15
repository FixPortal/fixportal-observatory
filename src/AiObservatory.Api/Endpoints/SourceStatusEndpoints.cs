using AiObservatory.Api.Services;
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

    /// <summary>
    /// Delegates to <see cref="SourceHealthClassifier"/>, which the daily digest reads too.
    /// Kept as a member here so the endpoint's own call site and its tests stay unchanged.
    /// </summary>
    public static string Classify(SourceSyncState state, Instant now) => SourceHealthClassifier.Classify(state, now);

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
