using System.Linq.Expressions;
using AiObservatory.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NodaTime;
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
    // not touch Id or RecordedAt (identity + chronological anchor). The cost
    // columns are the exception to last-write-wins: an incoming unknown cost (a
    // placeholder, cost 0) never replaces a row whose cost is already known, so a
    // delayed placeholder re-post cannot erase a measured figure. The cost travels
    // with the model and token counts it was measured for, and the per-finding
    // ratio is recomputed against the incoming accepted count.
#pragma warning disable S3776 // One atomic UPDATE: the conditional SET expressions must stay together.
    private Task<int> UpdateMetricsAsync(AdversarialReviewRun run, CancellationToken ct)
    {
        // Branch on the incoming run here so every CASE pairs a NULL with the typed column, never
        // with an untyped null parameter (Postgres would infer text and reject the assignment).
        var perFinding = PerFindingExpression(run);
        return ctx
            .AdversarialReviewRuns.Where(r => r.RunId == run.RunId && r.Reviewer == run.Reviewer && r.Role == run.Role)
            .ExecuteUpdateAsync(
                s =>
                    s.SetProperty(r => r.Model, r => run.CostUnknown && !r.CostUnknown ? r.Model : run.Model)
                        .SetProperty(r => r.Repo, run.Repo)
                        .SetProperty(r => r.Summary, run.Summary)
                        .SetProperty(
                            r => r.InputTokens,
                            r => run.CostUnknown && !r.CostUnknown ? r.InputTokens : run.InputTokens
                        )
                        .SetProperty(
                            r => r.OutputTokens,
                            r => run.CostUnknown && !r.CostUnknown ? r.OutputTokens : run.OutputTokens
                        )
                        .SetProperty(r => r.CostUsd, r => run.CostUnknown && !r.CostUnknown ? r.CostUsd : run.CostUsd)
                        .SetProperty(r => r.CostUnknown, r => run.CostUnknown && r.CostUnknown)
                        .SetProperty(r => r.ReviewDurationMs, run.ReviewDurationMs)
                        .SetProperty(r => r.IssuesRaised, run.IssuesRaised)
                        .SetProperty(r => r.IssuesAccepted, run.IssuesAccepted)
                        .SetProperty(r => r.CostPerAcceptedFinding, perFinding)
                        .SetProperty(r => r.ChunkCount, run.ChunkCount),
                ct
            );
    }
#pragma warning restore S3776

    // Incoming unknown over a known row: recompute against the incoming accepted count (null when
    // none). Incoming unknown over an unknown row keeps the stored null.
    private static Expression<Func<AdversarialReviewRun, decimal?>> PerFindingExpression(AdversarialReviewRun run)
    {
        var accepted = run.IssuesAccepted;
        if (!run.CostUnknown)
        {
            return r => run.CostPerAcceptedFinding;
        }

        if (accepted > 0)
        {
            return r => !r.CostUnknown ? r.CostUsd / accepted : r.CostPerAcceptedFinding;
        }

        return r => !r.CostUnknown ? null : r.CostPerAcceptedFinding;
    }

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

    public async Task<IReadOnlyList<AdversarialReviewRun>> GetRunsRecordedBetweenAsync(
        Instant from,
        Instant toExclusive,
        CancellationToken ct = default
    ) =>
        await ctx
            .AdversarialReviewRuns.AsNoTracking()
            .Where(r => r.RecordedAt >= from && r.RecordedAt < toExclusive)
            .OrderByDescending(r => r.RecordedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<AdversarialReviewFinding>> GetFindingsForRunsAsync(
        IReadOnlyCollection<string> runIds,
        CancellationToken ct = default
    ) =>
        await ctx
            .AdversarialReviewFindings.AsNoTracking()
            .Include(f => f.Participants)
            .Where(f => runIds.Contains(f.RunId))
            .OrderBy(f => f.RunId)
            .ThenBy(f => f.ChunkId)
            .ThenBy(f => f.FindingId)
            .ToListAsync(ct);

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
            .Select(ToStats)
            .OrderBy(s => s.Reviewer)
            .ThenBy(s => s.Model)
            .ThenBy(s => s.Role)
            .ToList();
    }

    private static AdversarialReviewStats ToStats(
        IGrouping<(string Reviewer, string Model, string Role), AdversarialReviewRun> g
    )
    {
        // Cost figures use only runs whose cost is known: an unknown cost is stored as 0 and
        // would otherwise drag the average down and make the reviewer look free.
        var known = g.Where(r => !r.CostUnknown).ToList();
        var knownAccepted = known.Sum(r => r.IssuesAccepted);
        return new AdversarialReviewStats(
            g.Key.Reviewer,
            g.Key.Model,
            g.Key.Role,
            g.Count(),
            known.Count > 0 ? known.Average(r => r.CostUsd) : null,
            g.Average(r => (double)r.IssuesRaised),
            g.Average(r => (double)r.IssuesAccepted),
            // Ratio of sums (total cost / total accepted), not a mean of each run's own
            // ratio — the latter weights a 1-finding run the same as a 50-finding run and
            // reads as inconsistent against avgCostPerRun / avgIssuesAccepted in the same
            // row. Null when no known-cost run accepted a finding.
            knownAccepted > 0
                ? known.Sum(r => r.CostUsd) / knownAccepted
                : null,
            g.Average(r => (double)r.ReviewDurationMs),
            g.Count(r => r.CostUnknown)
        );
    }

    // Finding writes are rare (one post per panel run), so every writer takes the same
    // transaction-scoped lock: concurrent replaces of one run cannot collide on the unique
    // index, and a delete cannot interleave with a replace and leave orphaned findings.
    private Task TakeFindingsLockAsync(CancellationToken ct) =>
        ctx.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext('adversarial-review-findings'))",
            ct
        );

    // A post is the whole run's finding set, so the old set goes first, in the same transaction:
    // a re-aggregation that drops or renumbers findings must leave no stale rows behind. The run
    // is re-checked under the lock, so a run deleted after the caller looked cannot be re-populated.
    public async Task<bool> ReplaceFindingsAsync(
        string runId,
        IReadOnlyList<AdversarialReviewFinding> findings,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(findings);

        await using var tx = await ctx.Database.BeginTransactionAsync(ct);
        await TakeFindingsLockAsync(ct);
        if (!await ctx.AdversarialReviewRuns.AnyAsync(r => r.RunId == runId, ct))
        {
            return false;
        }
        await ctx.AdversarialReviewFindings.Where(f => f.RunId == runId).ExecuteDeleteAsync(ct);
        ctx.AdversarialReviewFindings.AddRange(findings);
        await ctx.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<AdversarialReviewFinding>> GetFindingsAsync(
        string? runId = null,
        CancellationToken ct = default,
        Instant? since = null
    )
    {
        var query = ctx.AdversarialReviewFindings.AsNoTracking().Include(f => f.Participants).AsQueryable();
        if (runId is not null)
        {
            query = query.Where(f => f.RunId == runId);
        }
        if (since is { } floor)
        {
            query = query.Where(f => f.RecordedAt >= floor);
        }
        return await query.OrderBy(f => f.RunId).ThenBy(f => f.ChunkId).ThenBy(f => f.FindingId).ToListAsync(ct);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetRepositoriesByRunAsync(
        IReadOnlyCollection<string> runIds,
        CancellationToken ct = default
    )
    {
        if (runIds.Count == 0)
        {
            return new Dictionary<string, string>();
        }
        var rows = await ctx
            .AdversarialReviewRuns.AsNoTracking()
            .Where(r => r.Repo != null && runIds.Contains(r.RunId))
            .Select(r => new { r.RunId, Repo = r.Repo! })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.RunId).ToDictionary(g => g.Key, g => g.First().Repo);
    }

    public async Task<int> DeleteAllRunsAsync(CancellationToken ct = default)
    {
        await using var tx = await ctx.Database.BeginTransactionAsync(ct);
        await TakeFindingsLockAsync(ct);
        await ctx.AdversarialReviewFindings.ExecuteDeleteAsync(ct);
        var deleted = await ctx.AdversarialReviewRuns.ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
        return deleted;
    }

    // Removes every participant row for one sweep (all reviewers + judge sharing RunId), and its findings.
    public async Task<int> DeleteRunAsync(string runId, CancellationToken ct = default)
    {
        await using var tx = await ctx.Database.BeginTransactionAsync(ct);
        await TakeFindingsLockAsync(ct);
        await ctx.AdversarialReviewFindings.Where(f => f.RunId == runId).ExecuteDeleteAsync(ct);
        var deleted = await ctx.AdversarialReviewRuns.Where(r => r.RunId == runId).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
        return deleted;
    }
}
