using AiObservatory.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AiObservatory.Data.Repositories;

public class AdversarialReviewRepository(AiObservatoryDbContext ctx) : IAdversarialReviewRepository
{
    // Upsert keyed on the (RunId, Reviewer, Role) unique index. A re-emit for an
    // existing participant UPDATES its metrics in place (last-write-wins) rather
    // than being dropped as a duplicate — this is the correction path that lets a
    // run emitted with placeholder zeros (or a partial/incremental batch
    // aggregate) be backfilled with real numbers. RecordedAt is deliberately
    // preserved on update so a corrected run keeps its chronological position.
    public async Task<(Guid Id, bool Existed)> RecordRunAsync(AdversarialReviewRun run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        var existingId = await ctx
            .AdversarialReviewRuns.AsNoTracking()
            .Where(r => r.RunId == run.RunId && r.Reviewer == run.Reviewer && r.Role == run.Role)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(ct);

        if (existingId is not null)
        {
            await UpdateMetricsAsync(run, ct);
            return (existingId.Value, Existed: true);
        }

        ctx.AdversarialReviewRuns.Add(run);
        try
        {
            await ctx.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Lost an insert race with a concurrent emit of the same participant;
            // apply our values as an update so last-write-wins still holds.
            ctx.Entry(run).State = EntityState.Detached;
            await UpdateMetricsAsync(run, ct);
            var winnerId = await ctx
                .AdversarialReviewRuns.AsNoTracking()
                .Where(r => r.RunId == run.RunId && r.Reviewer == run.Reviewer && r.Role == run.Role)
                .Select(r => r.Id)
                .FirstAsync(ct);
            return (winnerId, Existed: true);
        }

        return (run.Id, Existed: false);
    }

    // Overwrite the mutable metric columns of the matched participant row. Does
    // not touch Id or RecordedAt (identity + chronological anchor).
    private Task<int> UpdateMetricsAsync(AdversarialReviewRun run, CancellationToken ct) =>
        ctx
            .AdversarialReviewRuns.Where(r => r.RunId == run.RunId && r.Reviewer == run.Reviewer && r.Role == run.Role)
            .ExecuteUpdateAsync(
                s =>
                    s.SetProperty(r => r.Model, run.Model)
                        .SetProperty(r => r.Repo, run.Repo)
                        .SetProperty(r => r.Summary, run.Summary)
                        .SetProperty(r => r.InputTokens, run.InputTokens)
                        .SetProperty(r => r.OutputTokens, run.OutputTokens)
                        .SetProperty(r => r.CostUsd, run.CostUsd)
                        .SetProperty(r => r.ReviewDurationMs, run.ReviewDurationMs)
                        .SetProperty(r => r.IssuesRaised, run.IssuesRaised)
                        .SetProperty(r => r.IssuesAccepted, run.IssuesAccepted)
                        .SetProperty(r => r.CostPerAcceptedFinding, run.CostPerAcceptedFinding)
                        .SetProperty(r => r.ChunkCount, run.ChunkCount),
                ct
            );

    public async Task<IReadOnlyList<AdversarialReviewRun>> GetRunsAsync(
        string? runId = null,
        CancellationToken ct = default
    )
    {
        var query = ctx.AdversarialReviewRuns.AsNoTracking();
        if (runId is not null)
        {
            query = query.Where(r => r.RunId == runId);
        }
        return await query.OrderByDescending(r => r.RecordedAt).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<AdversarialReviewStats>> GetStatsAsync(CancellationToken ct = default)
    {
        // Materialise then group in memory. This is a small audit table (one row
        // per reviewer per review run), so a server-side GROUP BY buys nothing —
        // and the previous IQueryable GroupBy projecting a record with a cast
        // inside Average (`(double)r.IssuesRaised`) did not translate to Postgres
        // and 500'd the endpoint, leaving the dashboard's review panel blank.
        // LINQ-to-Objects Average(decimal?) ignores nulls and yields null when all
        // are null, matching the intended semantics.
        var runs = await ctx.AdversarialReviewRuns.AsNoTracking().ToListAsync(ct);

        return runs.GroupBy(r => (r.Reviewer, r.Model, r.Role))
            .Select(g => new AdversarialReviewStats(
                g.Key.Reviewer,
                g.Key.Model,
                g.Key.Role,
                g.Count(),
                g.Average(r => r.CostUsd),
                g.Average(r => (double)r.IssuesRaised),
                g.Average(r => (double)r.IssuesAccepted),
                // Ratio of sums (total cost / total accepted), not a mean of each run's own
                // ratio — the latter weights a 1-finding run the same as a 50-finding run and
                // reads as inconsistent against avgCostPerRun / avgIssuesAccepted in the same
                // row. Null only when every run in the group had zero accepted findings.
                g.Sum(r => r.IssuesAccepted) > 0
                    ? g.Sum(r => r.CostUsd) / g.Sum(r => r.IssuesAccepted)
                    : null,
                g.Average(r => (double)r.ReviewDurationMs)
            ))
            .OrderBy(s => s.Reviewer)
            .ThenBy(s => s.Model)
            .ThenBy(s => s.Role)
            .ToList();
    }

    public Task<int> DeleteAllRunsAsync(CancellationToken ct = default) =>
        ctx.AdversarialReviewRuns.ExecuteDeleteAsync(ct);

    // Removes every participant row for one sweep (all reviewers + judge sharing RunId).
    public Task<int> DeleteRunAsync(string runId, CancellationToken ct = default) =>
        ctx.AdversarialReviewRuns.Where(r => r.RunId == runId).ExecuteDeleteAsync(ct);
}
