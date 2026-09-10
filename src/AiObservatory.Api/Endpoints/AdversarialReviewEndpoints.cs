using AiObservatory.Api.Services;
using AiObservatory.Data.Repositories;

namespace AiObservatory.Api.Endpoints;

public static class AdversarialReviewEndpoints
{
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
