using AiObservatory.Data;
using AiObservatory.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace AiObservatory.Api.Services.Suggestions;

public enum SuggestionChange
{
    Changed,
    NotFound,
    Resolved,
}

/// <summary>
/// Persists suggestions. Every writer takes the same transaction-scoped advisory lock, so the daily
/// run, a manual refresh and a dismiss or snooze serialise instead of racing on the unique key.
/// </summary>
public class SuggestionStore(AiObservatoryDbContext db, IClock clock)
{
    private Task LockAsync(CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext('suggestions'))", ct);

    /// <summary>
    /// Takes the lock first, then produces the outcomes and applies them, so an evaluation that loaded
    /// its inputs earlier cannot overwrite a newer one. Returns the outcomes it applied.
    /// </summary>
    public virtual async Task<IReadOnlyList<RuleOutcome>> EvaluateAndSaveAsync(
        Func<CancellationToken, Task<IReadOnlyList<RuleOutcome>>> evaluate,
        SuggestionOptions options,
        CancellationToken ct = default
    )
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(ct);

        var outcomes = await evaluate(ct);
        var existing = await db.Suggestions.ToListAsync(ct);
        var result = SuggestionLifecycle.Reconcile(existing, outcomes, clock.GetCurrentInstant(), options);
        db.Suggestions.AddRange(result.Inserts);

        var stored = await db.SuggestionRuleRuns.ToDictionaryAsync(r => r.RuleId, StringComparer.Ordinal, ct);
        foreach (var run in result.RuleRuns)
        {
            if (stored.TryGetValue(run.RuleId, out var row))
            {
                row.EvaluatedAt = run.EvaluatedAt;
                row.Status = run.Status;
                row.Reason = run.Reason;
                row.SubjectsEvaluated = run.SubjectsEvaluated;
                row.SuggestionsEmitted = run.SuggestionsEmitted;
            }
            else
            {
                db.SuggestionRuleRuns.Add(run);
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return outcomes;
    }

    public async Task<IReadOnlyList<Suggestion>> ListAsync(string? status, CancellationToken ct = default)
    {
        var query = db.Suggestions.AsNoTracking().AsQueryable();
        if (status is not null)
        {
            query = query.Where(s => s.Status == status);
        }
        return await query.OrderBy(s => s.FirstSeenAt).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<SuggestionRuleRun>> ListRuleRunsAsync(CancellationToken ct = default) =>
        await db.SuggestionRuleRuns.AsNoTracking().OrderBy(r => r.RuleId).ToListAsync(ct);

    /// <summary>
    /// Applies a dismiss or snooze under the lock. A resolved row is not changed: the owner never saw
    /// it as open, and dismissing it would hide the condition when it next fires.
    /// </summary>
    public async Task<SuggestionChange> ChangeAsync(Guid id, Action<Suggestion> change, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(ct);

        var row = await db.Suggestions.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (row is null)
        {
            return SuggestionChange.NotFound;
        }
        if (row.Status == "resolved")
        {
            return SuggestionChange.Resolved;
        }
        change(row);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return SuggestionChange.Changed;
    }
}
