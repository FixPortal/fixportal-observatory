using AiObservatory.Data;
using AiObservatory.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;

namespace AiObservatory.Api.Endpoints;

// Response record properties are consumed by ASP.NET Core JSON serialization, while
// request records are instantiated by model binding.
// ReSharper disable NotAccessedPositionalProperty.Global
// ReSharper disable ClassNeverInstantiated.Global

public static class CavemanEndpoints
{
    public static void MapCavemanEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/caveman-sessions", UpsertCavemanSessionsAsync);
        app.MapGet("/caveman-stats", GetCavemanStatsAsync);
    }

    private static async Task<IResult> UpsertCavemanSessionsAsync(
        CavemanSessionsRequest req,
        AiObservatoryDbContext db,
        IClock clock,
        CancellationToken ct
    )
    {
        if (req.Sessions is not { Count: > 0 })
        {
            return Results.Ok(new { Upserted = 0 });
        }

        if (req.Sessions.Count > 1000)
        {
            return Results.BadRequest("Cannot upsert more than 1000 sessions at once.");
        }

        var now = clock.GetCurrentInstant();
        var validationError = ValidateSessions(req.Sessions, now);
        if (validationError is not null)
        {
            return Results.BadRequest(validationError);
        }

        var sessionIds = req.Sessions.Select(s => s.SessionId).ToList();
        var existingSessionIds = await db
            .CavemanSessions.Where(s => sessionIds.Contains(s.SessionId))
            .Select(s => s.SessionId)
            .ToHashSetAsync(ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var upserted = await UpsertSessionsAsync(req.Sessions, existingSessionIds, db, ct);

        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Concurrent insert of one of these SessionIds; LWW makes a retry converge.
            await tx.RollbackAsync(ct);
            return Results.Conflict("Concurrent write to one or more sessions; retry.");
        }
        return Results.Ok(new { Upserted = upserted });
    }

    private static string? ValidateSessions(IReadOnlyCollection<CavemanSessionRequest> sessions, Instant now)
    {
        var seenSessionIds = new HashSet<string>();
        foreach (var session in sessions)
        {
            if (string.IsNullOrWhiteSpace(session.SessionId) || session.SessionId.Length > 200)
            {
                return $"SessionId invalid: '{session.SessionId}'";
            }
            if (session.OutputTokens < 0 || session.EstSavedTokens < 0 || session.EstSavedUsd < 0)
            {
                return "Token counts and cost must be non-negative.";
            }
            if (Instant.FromDateTimeOffset(session.OccurredAtUtc) > now + Duration.FromMinutes(5))
            {
                return $"OccurredAtUtc must not be in the future: {session.OccurredAtUtc}";
            }
            // In-batch dedup guard (mirrors ActivityEndpoints): two entries sharing a
            // not-yet-persisted SessionId would both insert and violate the unique index.
            if (!seenSessionIds.Add(session.SessionId))
            {
                return $"Duplicate SessionId in batch: '{session.SessionId}'";
            }
        }

        return null;
    }

    private static async Task<int> UpsertSessionsAsync(
        IEnumerable<CavemanSessionRequest> sessions,
        IReadOnlySet<string> existingSessionIds,
        AiObservatoryDbContext db,
        CancellationToken ct
    )
    {
        var upserted = 0;
        foreach (var session in sessions)
        {
            var occurredAt = Instant.FromDateTimeOffset(session.OccurredAtUtc);

            if (existingSessionIds.Contains(session.SessionId))
            {
                // Evaluate LWW against the live row so a concurrent newer write cannot
                // be regressed by this request's preloaded existence snapshot.
                var updated = await db
                    .CavemanSessions.Where(x => x.SessionId == session.SessionId && x.OccurredAt <= occurredAt)
                    .ExecuteUpdateAsync(
                        upd =>
                            upd.SetProperty(p => p.OccurredAt, occurredAt)
                                .SetProperty(p => p.Mode, session.Mode)
                                .SetProperty(p => p.Model, session.Model)
                                .SetProperty(p => p.OutputTokens, session.OutputTokens)
                                .SetProperty(p => p.EstSavedTokens, session.EstSavedTokens)
                                .SetProperty(p => p.EstSavedUsd, session.EstSavedUsd),
                        ct
                    );
                if (updated == 0)
                {
                    continue;
                }
            }
            else
            {
                db.CavemanSessions.Add(
                    new CavemanSession
                    {
                        SessionId = session.SessionId,
                        OccurredAt = occurredAt,
                        Mode = session.Mode,
                        Model = session.Model,
                        OutputTokens = session.OutputTokens,
                        EstSavedTokens = session.EstSavedTokens,
                        EstSavedUsd = session.EstSavedUsd,
                    }
                );
            }
            upserted++;
        }

        return upserted;
    }

    private static async Task<IResult> GetCavemanStatsAsync(AiObservatoryDbContext db, CancellationToken ct)
    {
        var stats = await db
            .CavemanSessions.GroupBy(s => true)
            .Select(g => new CavemanStatsResponse(
                g.Count(),
                g.Sum(s => s.OutputTokens),
                g.Sum(s => s.EstSavedTokens),
                g.Sum(s => s.EstSavedUsd)
            ))
            .FirstOrDefaultAsync(ct);

        return Results.Ok(stats ?? new CavemanStatsResponse(0, 0, 0, 0m));
    }
}

public sealed record CavemanSessionRequest(
    string SessionId,
    DateTimeOffset OccurredAtUtc,
    string? Mode,
    string? Model,
    long OutputTokens,
    long EstSavedTokens,
    decimal EstSavedUsd
);

public sealed record CavemanSessionsRequest(List<CavemanSessionRequest> Sessions);

public sealed record CavemanStatsResponse(
    int Sessions,
    long TotalOutputTokens,
    long TotalEstSavedTokens,
    decimal TotalEstSavedUsd
);
