using System.Globalization;
using System.Linq.Expressions;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Text;
using Npgsql;

namespace AiObservatory.Api.Endpoints;

// Request records are instantiated by ASP.NET Core model binding; response record
// properties are consumed by JSON serialization.
// ReSharper disable ClassNeverInstantiated.Global
// ReSharper disable NotAccessedPositionalProperty.Global

public static class ActivityEndpoints
{
    public static void MapActivityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/activity/sessions", UpsertActivitySessionsAsync);

        app.MapGet(
                "/activity/daily",
                async (
                    AiObservatoryDbContext db,
                    IClock clock,
                    IOptions<ActivityOptions> activityOptions,
                    string? from,
                    string? to,
                    CancellationToken ct
                ) =>
                {
                    var today = clock.GetCurrentInstant().InUtc().Date;
                    if (!TryParseDateRange(from, to, today, out var start, out var end, out var error))
                    {
                        return error!;
                    }

                    var startInstant = start.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();
                    var endInstant = end.PlusDays(1).AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();

                    var sessions = await db
                        .ClaudeActivitySessions.AsNoTracking()
                        .Where(s => s.LastSeenAt > startInstant && s.StartedAt < endInstant)
                        .Where(AllowedProjectPredicate(activityOptions.Value.ProjectOwners))
                        .Select(s => new ActivitySessionSlice(s.Project, s.StartedAt, s.LastSeenAt, s.ActiveSeconds))
                        .ToListAsync(ct);

                    var byDate = BuildDailyActivityResponses(sessions, start, end, activityOptions.Value.ProjectOwners);

                    return Results.Ok(byDate);
                }
            )
            .AddEndpointFilter<AdminOnlyApiKeyEndpointFilter>();

        app.MapGet(
                "/activity/by-project",
                async (
                    AiObservatoryDbContext db,
                    IClock clock,
                    IOptions<ActivityOptions> activityOptions,
                    string? from,
                    string? to,
                    CancellationToken ct
                ) =>
                {
                    var today = clock.GetCurrentInstant().InUtc().Date;
                    if (!TryParseDateRange(from, to, today, out var start, out var end, out var error))
                    {
                        return error!;
                    }

                    var startInstant = start.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();
                    var endInstant = end.PlusDays(1).AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();

                    var sessions = await db
                        .ClaudeActivitySessions.AsNoTracking()
                        .Where(s => s.StartedAt >= startInstant && s.StartedAt < endInstant)
                        .Where(AllowedProjectPredicate(activityOptions.Value.ProjectOwners))
                        .Select(s => new
                        {
                            s.SessionId,
                            s.Project,
                            s.ActiveSeconds,
                        })
                        .ToListAsync(ct);

                    var totalSeconds = sessions.Sum(s => s.ActiveSeconds);

                    var byProject = sessions
                        .GroupBy(s => s.Project)
                        .Select(g => new ProjectActivityResponse(
                            g.Key,
                            g.Select(s => s.SessionId).Distinct().Count(),
                            g.Sum(s => s.ActiveSeconds),
                            totalSeconds > 0 ? Math.Round(g.Sum(s => s.ActiveSeconds) * 100.0 / totalSeconds, 1) : 0
                        ))
                        .OrderByDescending(p => p.ActiveSeconds)
                        .ToList();

                    return Results.Ok(byProject);
                }
            )
            .AddEndpointFilter<AdminOnlyApiKeyEndpointFilter>();

        // One-off cleanup for pre-allowlist ingestion noise (scratch dirs, other
        // orgs, non-git leaf-folder fallbacks like "claude-review"). Irreversible.
        // Admin-key gated, mirrors the /aggregates provider-scoped reset.
        app.MapDelete(
                "/activity/sessions/disallowed-projects",
                async (AiObservatoryDbContext db, IOptions<ActivityOptions> activityOptions, CancellationToken ct) =>
                {
                    var deleted = await DeleteDisallowedProjectSessionsAsync(
                        db,
                        activityOptions.Value.ProjectOwners,
                        ct
                    );

                    return Results.Ok(new { deletedSessions = deleted });
                }
            )
            .AddEndpointFilter<AdminOnlyApiKeyEndpointFilter>();
    }

    private static async Task<IResult> UpsertActivitySessionsAsync(
        ActivitySessionsRequest req,
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
            .ClaudeActivitySessions.Where(s => sessionIds.Contains(s.SessionId))
            .Select(s => s.SessionId)
            .ToHashSetAsync(ct);

        // One transaction so the ExecuteUpdateAsync writes and the deferred inserts commit
        // atomically — a unique-violation race on a concurrently-inserted SessionId rolls
        // the whole batch back rather than leaving it half-applied.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var upserted = await UpsertSessionsAsync(req.Sessions, existingSessionIds, db, now, ct);

        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent request inserted one of these SessionIds between the existence
            // snapshot and SaveChanges. The merge is monotonic, so a retry converges.
            await tx.RollbackAsync(ct);
            return Results.Conflict("Concurrent write to one or more sessions; retry.");
        }
        return Results.Ok(new { Upserted = upserted });
    }

    private static string? ValidateSessions(IReadOnlyCollection<ActivitySessionRequest> sessions, Instant now)
    {
        var seenSessionIds = new HashSet<string>();
        foreach (var session in sessions)
        {
            var error = ValidateSession(session, now);
            if (error is not null)
            {
                return error;
            }
            if (!seenSessionIds.Add(session.SessionId))
            {
                return $"Duplicate SessionId in batch: '{session.SessionId}'";
            }
        }

        return null;
    }

    private static string? ValidateSession(ActivitySessionRequest session, Instant now)
    {
        if (string.IsNullOrWhiteSpace(session.SessionId) || session.SessionId.Length > 200)
        {
            return $"SessionId invalid: '{session.SessionId}'";
        }
        if (string.IsNullOrWhiteSpace(session.Project) || session.Project.Length > 200)
        {
            return $"Project invalid: '{session.Project}'";
        }
        if (session.ActiveSeconds < 0)
        {
            return "ActiveSeconds must be non-negative.";
        }
        if (session.LastSeenAtUtc < session.StartedAtUtc)
        {
            return $"LastSeenAtUtc must not be before StartedAtUtc: '{session.SessionId}'";
        }

        // A bad row must fail before any earlier row is committed because
        // ExecuteUpdateAsync writes immediately.
        var startedAt = Instant.FromDateTimeOffset(session.StartedAtUtc);
        var lastSeenAt = Instant.FromDateTimeOffset(session.LastSeenAtUtc);
        if (startedAt > now + Duration.FromMinutes(5) || lastSeenAt > now + Duration.FromMinutes(5))
        {
            return $"Timestamps must not be in the future: '{session.SessionId}'";
        }

        return session.ActiveSeconds > (lastSeenAt - startedAt).TotalSeconds
            ? $"ActiveSeconds exceeds elapsed time for session '{session.SessionId}'"
            : null;
    }

    private static async Task<int> UpsertSessionsAsync(
        IEnumerable<ActivitySessionRequest> sessions,
        IReadOnlySet<string> existingSessionIds,
        AiObservatoryDbContext db,
        Instant now,
        CancellationToken ct
    )
    {
        var upserted = 0;
        foreach (var session in sessions)
        {
            if (existingSessionIds.Contains(session.SessionId))
            {
                if (!await UpdateExistingSessionAsync(session, db, ct))
                {
                    continue;
                }
            }
            else
            {
                db.ClaudeActivitySessions.Add(
                    new ClaudeActivitySession
                    {
                        SessionId = session.SessionId,
                        Project = session.Project,
                        StartedAt = Instant.FromDateTimeOffset(session.StartedAtUtc),
                        LastSeenAt = Instant.FromDateTimeOffset(session.LastSeenAtUtc),
                        ActiveSeconds = session.ActiveSeconds,
                        IngestedAt = now,
                    }
                );
            }
            upserted++;
        }

        return upserted;
    }

    private static async Task<bool> UpdateExistingSessionAsync(
        ActivitySessionRequest session,
        AiObservatoryDbContext db,
        CancellationToken ct
    )
    {
        var lastSeenAt = Instant.FromDateTimeOffset(session.LastSeenAtUtc);

        // Evaluate freshness and merge against the live row in one statement so
        // a concurrent newer write cannot be regressed by this request's snapshot.
        var updated = await db
            .ClaudeActivitySessions.Where(x =>
                x.SessionId == session.SessionId
                && (x.ActiveSeconds < session.ActiveSeconds || x.LastSeenAt < lastSeenAt)
            )
            .ExecuteUpdateAsync(
                upd =>
                    upd.SetProperty(
                            p => p.ActiveSeconds,
                            p => p.ActiveSeconds > session.ActiveSeconds ? p.ActiveSeconds : session.ActiveSeconds
                        )
                        .SetProperty(p => p.LastSeenAt, p => p.LastSeenAt > lastSeenAt ? p.LastSeenAt : lastSeenAt),
                ct
            );

        return updated > 0;
    }

    // The owner allowlist is configuration, not code — see ActivityOptions.ProjectOwners for
    // what it means and why empty allows everything. It used to be a hardcoded
    // ["FixPortal", "fix-portal"], which left every other deployment with two blank tabs.

    public sealed record ActivitySessionSlice(
        string Project,
        Instant StartedAt,
        Instant LastSeenAt,
        long ActiveSeconds
    );

    // Single source for the SQL-translatable allowlist rule, reused by every EF query below
    // instead of each carrying its own copy of the Any(...)/StartsWith(...) text. Built per call
    // from the configured owners rather than held as a static, so nothing has to mutate shared
    // state to change the policy. IsAllowedProject (below) intentionally stays a separate,
    // ordinal-comparison implementation for the in-memory path — see its own comment.
    public static Expression<Func<ClaudeActivitySession, bool>> AllowedProjectPredicate(IReadOnlyList<string> owners)
    {
        ArgumentNullException.ThrowIfNull(owners);
        // No allowlist configured means no filter at all — see ActivityOptions.ProjectOwners.
        // Expressed as a constant-true predicate rather than an empty Any(...), which would
        // reject everything.
        return owners.Count == 0 ? _ => true : s => owners.Any(o => s.Project == o || s.Project.StartsWith(o + "/"));
    }

    // Negation of AllowedProjectPredicate, built from the same expression body so the
    // "disallowed" side of the rule can never drift from the "allowed" side.
    private static Expression<Func<ClaudeActivitySession, bool>> DisallowedProjectPredicate(
        IReadOnlyList<string> owners
    )
    {
        var allowed = AllowedProjectPredicate(owners);
        return Expression.Lambda<Func<ClaudeActivitySession, bool>>(
            Expression.Not(allowed.Body),
            allowed.Parameters[0]
        );
    }

    // In-memory counterpart of AllowedProjectPredicate. Kept as its own implementation (not
    // derived from the expression above) because it deliberately uses an ordinal StartsWith —
    // culture-sensitive comparison would be wrong here — whereas SQL translation via Npgsql is
    // byte/ordinal-equivalent regardless.
    public static bool IsAllowedProject(string project, IReadOnlyList<string> owners)
    {
        ArgumentNullException.ThrowIfNull(owners);
        return owners.Count == 0
            || owners.Any(o => project == o || project.StartsWith(o + "/", StringComparison.Ordinal));
    }

    public static Task<int> DeleteDisallowedProjectSessionsAsync(
        AiObservatoryDbContext db,
        IReadOnlyList<string> owners,
        CancellationToken ct = default
    ) => db.ClaudeActivitySessions.Where(DisallowedProjectPredicate(owners)).ExecuteDeleteAsync(ct);

    public static List<DailyActivityResponse> BuildDailyActivityResponses(
        IEnumerable<ActivitySessionSlice> sessions,
        LocalDate start,
        LocalDate end,
        IReadOnlyList<string> owners
    )
    {
        var startInstant = start.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();
        var endInstant = end.PlusDays(1).AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();
        var slices = new List<(LocalDate Date, Instant Start, Instant End, long ActiveSeconds)>();

        foreach (var session in sessions.Where(s => IsAllowedProject(s.Project, owners) && s.LastSeenAt > s.StartedAt))
        {
            var clippedStart = Max(session.StartedAt, startInstant);
            var clippedEnd = Min(session.LastSeenAt, endInstant);
            if (clippedEnd <= clippedStart)
            {
                continue;
            }

            var totalSeconds = (session.LastSeenAt - session.StartedAt).TotalSeconds;
            var cursor = clippedStart;
            while (cursor < clippedEnd)
            {
                var date = cursor.InUtc().Date;
                var nextMidnight = date.PlusDays(1).AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();
                var fragmentEnd = Min(clippedEnd, nextMidnight);
                var fragmentSeconds = (fragmentEnd - cursor).TotalSeconds;
                var activeSeconds =
                    totalSeconds > 0
                        ? (long)
                            Math.Round(
                                session.ActiveSeconds * fragmentSeconds / totalSeconds,
                                MidpointRounding.AwayFromZero
                            )
                        : 0;
                slices.Add((date, cursor, fragmentEnd, activeSeconds));
                cursor = fragmentEnd;
            }
        }

        return slices
            .GroupBy(s => s.Date)
            .Select(g => new DailyActivityResponse(
                LocalDatePattern.Iso.Format(g.Key),
                g.Sum(s => s.ActiveSeconds),
                MergeIntervalSeconds(g.Select(s => (s.Start, s.End)))
            ))
            .OrderBy(d => d.Date)
            .ToList();
    }

    // Wall-clock time actually spent: merges overlapping session spans instead of
    // summing them, so N parallel sessions covering the same hour count as one hour.
    public static long MergeIntervalSeconds(IEnumerable<(Instant Start, Instant End)> spans)
    {
        var ordered = spans.Where(s => s.End > s.Start).OrderBy(s => s.Start).ToList();
        if (ordered.Count == 0)
        {
            return 0;
        }

        var total = Duration.Zero;
        var (curStart, curEnd) = ordered[0];
        foreach (var (start, end) in ordered.Skip(1))
        {
            if (start > curEnd)
            {
                total += curEnd - curStart;
                (curStart, curEnd) = (start, end);
            }
            else if (end > curEnd)
            {
                curEnd = end;
            }
        }
        total += curEnd - curStart;
        return (long)total.TotalSeconds;
    }

    private static Instant Min(Instant a, Instant b) => a < b ? a : b;

    private static Instant Max(Instant a, Instant b) => a > b ? a : b;

    public static bool TryParseDateRange(
        string? from,
        string? to,
        LocalDate today,
        out LocalDate start,
        out LocalDate end,
        out IResult? error
    )
    {
        error = null;
        // Thirty calendar days inclusive of today — the same default /aggregates uses.
        start = today.PlusDays(-29);
        end = today;

        if (from is not null)
        {
            if (
                !DateOnly.TryParseExact(
                    from,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var fromDate
                )
            )
            {
                error = Results.BadRequest("from must be yyyy-MM-dd");
                return false;
            }
            start = LocalDate.FromDateOnly(fromDate);
        }

        if (to is not null)
        {
            if (
                !DateOnly.TryParseExact(
                    to,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var toDate
                )
            )
            {
                error = Results.BadRequest("to must be yyyy-MM-dd");
                return false;
            }
            end = LocalDate.FromDateOnly(toDate);
        }

        if (start > end)
        {
            error = Results.BadRequest("from must be on or before to");
            return false;
        }

        return true;
    }
}

public sealed record ActivitySessionRequest(
    string SessionId,
    string Project,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset LastSeenAtUtc,
    long ActiveSeconds
);

public sealed record ActivitySessionsRequest(List<ActivitySessionRequest> Sessions);

public sealed record DailyActivityResponse(string Date, long ActiveSeconds, long WallClockSeconds);

public sealed record ProjectActivityResponse(string Project, int SessionCount, long ActiveSeconds, double SharePercent);
