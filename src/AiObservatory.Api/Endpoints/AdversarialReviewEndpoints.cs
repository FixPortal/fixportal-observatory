using AiObservatory.Api.Services;
using AiObservatory.Data.Repositories;
using NodaTime;

namespace AiObservatory.Api.Endpoints;

public static class AdversarialReviewEndpoints
{
    // Three times the Seats tab's 60-day window: wide enough for any view the dashboard offers.
    private const int DefaultFindingsWindowDays = 180;

    // Returning the builder is the standard fluent endpoint-mapping convention.
    // ReSharper disable once UnusedMethodReturnValue.Global
    public static IEndpointRouteBuilder MapAdversarialReviewEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(
                "/adversarial-review/runs",
                (AdversarialReviewRunRequest req, AdversarialReviewService svc, HttpContext ctx) =>
                    svc.RecordRunAsync(req, ctx.RequestAborted)
            )
            .WithName("RecordAdversarialReviewRun");

        app.MapGet(
            "/adversarial-review/runs",
            async (string? runId, IAdversarialReviewRepository repo, HttpContext ctx) =>
            {
                var runs = await repo.GetRunsAsync(runId, ctx.RequestAborted);
                return Results.Ok(
                    runs.Select(r => new
                    {
                        r.Id,
                        r.Reviewer,
                        r.Model,
                        r.Role,
                        r.Repo,
                        r.Summary,
                        r.InputTokens,
                        r.OutputTokens,
                        r.CostUsd,
                        r.CostUnknown,
                        r.ReviewDurationMs,
                        r.IssuesRaised,
                        r.IssuesAccepted,
                        r.CostPerAcceptedFinding,
                        r.ChunkCount,
                        r.RunId,
                        recordedAt = r.RecordedAt.ToString(),
                    })
                );
            }
        );

        app.MapPost(
                "/adversarial-review/findings",
                (AdversarialReviewFindingsRequest req, AdversarialReviewFindingService svc, HttpContext ctx) =>
                    svc.RecordFindingsAsync(req, ctx.RequestAborted)
            )
            .WithName("RecordAdversarialReviewFindings");

        app.MapGet(
            "/adversarial-review/findings",
            async (string? runId, DateOnly? since, IClock clock, IAdversarialReviewRepository repo, HttpContext ctx) =>
            {
                // A named run is returned whole. Otherwise the list is bounded to a window, so the
                // payload does not grow with every review ever recorded.
                Instant? floor = null;
                if (runId is null)
                {
                    var start =
                        since is { } day
                            ? new LocalDate(day.Year, day.Month, day.Day)
                            : clock.GetCurrentInstant().InUtc().Date.PlusDays(-DefaultFindingsWindowDays);
                    floor = start.AtMidnight().InUtc().ToInstant();
                }
                var findings = await repo.GetFindingsAsync(runId, ctx.RequestAborted, floor);
                var repos = await repo.GetRepositoriesByRunAsync(
                    findings.Select(f => f.RunId).Distinct().ToList(),
                    ctx.RequestAborted
                );
                return Results.Ok(
                    findings.Select(f => new
                    {
                        f.RunId,
                        repo = repos.GetValueOrDefault(f.RunId),
                        f.ChunkId,
                        f.FindingId,
                        f.Severity,
                        f.Outcome,
                        raisedBy = f
                            .Participants.Where(p => p.Kind == "raiser")
                            .Select(p => new { p.Reviewer, p.Model }),
                        assessments = f
                            .Participants.Where(p => p.Kind == "assessor")
                            .Select(p => new
                            {
                                p.Reviewer,
                                p.Model,
                                p.Verdict,
                            }),
                    })
                );
            }
        );

        app.MapGet(
            "/adversarial-review/stats",
            async (IAdversarialReviewRepository repo, HttpContext ctx) =>
            {
                var stats = await repo.GetStatsAsync(ctx.RequestAborted);
                return Results.Ok(stats);
            }
        );

        // Purge ALL adversarial-review runs. Admin-key gated by the /api group's
        // ApiKeyEndpointFilter (non-GET requires the admin key). Irreversible —
        // used to reset the table for a clean re-track.
        app.MapDelete(
            "/adversarial-review/runs",
            async (IAdversarialReviewRepository repo, HttpContext ctx) =>
            {
                var deleted = await repo.DeleteAllRunsAsync(ctx.RequestAborted);
                return Results.Ok(new { deleted });
            }
        );

        // Removes one sweep (all participant rows sharing runId). Admin-key gated
        // by the /api group's ApiKeyEndpointFilter, same as the full purge above.
        app.MapDelete(
            "/adversarial-review/runs/{runId}",
            async (string runId, IAdversarialReviewRepository repo, HttpContext ctx) =>
            {
                var deleted = await repo.DeleteRunAsync(runId, ctx.RequestAborted);
                return deleted == 0 ? Results.NotFound() : Results.Ok(new { deleted });
            }
        );

        return app;
    }
}
