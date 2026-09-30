using AiObservatory.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;

namespace AiObservatory.Data.Repositories;

public sealed class BudgetAlertRepository(AiObservatoryDbContext ctx) : IBudgetAlertRepository
{
    private const int BudgetAlertEmailBatchSize = 50;

    public async Task<BudgetAlertClaimResult> GetOrCreateBudgetAlertAsync(
        Guid ruleId,
        LocalDate periodStart,
        LocalDate periodEnd,
        decimal thresholdGbp,
        decimal actualSpendGbp,
        Insight insight,
        Instant triggeredAt,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(insight);
        var existingClaim = await ctx
            .BudgetAlertClaims.AsNoTracking()
            .SingleOrDefaultAsync(
                candidate =>
                    candidate.BudgetRuleId == ruleId
                    && candidate.PeriodStart == periodStart
                    && candidate.PeriodEnd == periodEnd,
                ct
            );
        if (existingClaim is not null)
        {
            return new BudgetAlertClaimResult(
                existingClaim.Id,
                false,
                existingClaim.ThresholdGbp,
                existingClaim.ActualSpendGbp,
                existingClaim.CreatedAt
            );
        }

        var claim = new BudgetAlertClaim
        {
            BudgetRuleId = ruleId,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            InsightId = insight.Id,
            ThresholdGbp = thresholdGbp,
            ActualSpendGbp = actualSpendGbp,
            CreatedAt = triggeredAt,
        };

        await using var tx = await ctx.Database.BeginTransactionAsync(ct);
        try
        {
            ctx.Insights.Add(insight);
            ctx.BudgetAlertClaims.Add(claim);
            await ctx.SaveChangesAsync(ct);
            var updated = await ctx
                .BudgetRules.Where(rule => rule.Id == ruleId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(rule => rule.LastTriggeredAt, triggeredAt), ct);
            if (updated != 1)
            {
                throw new InvalidOperationException($"Budget rule {ruleId} no longer exists.");
            }

            await tx.CommitAsync(ct);
            return new BudgetAlertClaimResult(claim.Id, true, thresholdGbp, actualSpendGbp, triggeredAt);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException
                    is PostgresException
                    {
                        SqlState: PostgresErrorCodes.UniqueViolation,
                        ConstraintName: "UX_BudgetAlertClaims_RulePeriod",
                    }
            )
        {
            await tx.RollbackAsync(ct);
            ctx.Entry(claim).State = EntityState.Detached;
            ctx.Entry(insight).State = EntityState.Detached;
            var existing = await ctx
                .BudgetAlertClaims.AsNoTracking()
                .SingleAsync(
                    candidate =>
                        candidate.BudgetRuleId == ruleId
                        && candidate.PeriodStart == periodStart
                        && candidate.PeriodEnd == periodEnd,
                    ct
                );
            return new BudgetAlertClaimResult(
                existing.Id,
                false,
                existing.ThresholdGbp,
                existing.ActualSpendGbp,
                existing.CreatedAt
            );
        }
        catch
        {
            await tx.RollbackAsync(ct);
            ctx.Entry(claim).State = EntityState.Detached;
            ctx.Entry(insight).State = EntityState.Detached;
            throw;
        }
    }

    public async Task<bool> TryAcquireBudgetAlertEmailLeaseAsync(
        Guid claimId,
        Guid leaseId,
        Instant acquiredAt,
        Instant leaseExpiredBefore,
        CancellationToken ct = default
    ) =>
        await ctx
            .BudgetAlertClaims.Where(claim =>
                claim.Id == claimId
                && claim.EmailSentAt == null
                && (claim.EmailLeaseAcquiredAt == null || claim.EmailLeaseAcquiredAt <= leaseExpiredBefore)
            )
            .ExecuteUpdateAsync(
                setters =>
                    setters
                        .SetProperty(claim => claim.EmailLeaseId, leaseId)
                        .SetProperty(claim => claim.EmailLeaseAcquiredAt, acquiredAt),
                ct
            ) == 1;

    public async Task<IReadOnlyList<BudgetAlertEmail>> GetDeliverableBudgetAlertEmailsAsync(
        Instant leaseExpiredBefore,
        Instant createdOnOrAfter,
        CancellationToken ct = default
    ) =>
        // The CreatedAt floor age-bounds the pending set: a claim that can never be delivered
        // (nothing configured, a terminally dead channel) would otherwise be re-leased and
        // re-warned on every pass forever, starving newer claims behind the batch-size Take and
        // bursting every historical alert at an operator who configures a channel late. Older
        // claims are left pending, not closed — a channel configured inside the age window
        // still receives them.
        await (
            from claim in ctx.BudgetAlertClaims.AsNoTracking()
            join rule in ctx.BudgetRules.AsNoTracking() on claim.BudgetRuleId equals rule.Id
            where
                claim.EmailSentAt == null
                && claim.CreatedAt >= createdOnOrAfter
                && (claim.EmailLeaseAcquiredAt == null || claim.EmailLeaseAcquiredAt <= leaseExpiredBefore)
            orderby claim.CreatedAt, claim.Id
            select new BudgetAlertEmail(
                claim.Id,
                claim.BudgetRuleId,
                rule.Provider,
                rule.Period,
                claim.PeriodStart,
                claim.PeriodEnd,
                claim.ThresholdGbp,
                claim.ActualSpendGbp,
                claim.CreatedAt
            )
        )
            .Take(BudgetAlertEmailBatchSize)
            .ToListAsync(ct);

    public async Task ReleaseBudgetAlertEmailLeaseAsync(Guid claimId, Guid leaseId, CancellationToken ct = default)
    {
        await ctx
            .BudgetAlertClaims.Where(claim =>
                claim.Id == claimId && claim.EmailSentAt == null && claim.EmailLeaseId == leaseId
            )
            .ExecuteUpdateAsync(
                setters =>
                    setters
                        .SetProperty(claim => claim.EmailLeaseId, (Guid?)null)
                        .SetProperty(claim => claim.EmailLeaseAcquiredAt, (Instant?)null),
                ct
            );
    }

    public async Task MarkBudgetAlertEmailSentAsync(
        Guid claimId,
        Guid leaseId,
        Instant sentAt,
        CancellationToken ct = default
    )
    {
        await ctx
            .BudgetAlertClaims.Where(claim =>
                claim.Id == claimId && claim.EmailSentAt == null && claim.EmailLeaseId == leaseId
            )
            .ExecuteUpdateAsync(setters => setters.SetProperty(claim => claim.EmailSentAt, sentAt), ct);
    }

    public async Task<bool> GetBudgetAlertSlackSentAsync(Guid claimId, CancellationToken ct = default) =>
        await ctx
            .BudgetAlertClaims.AsNoTracking()
            .Where(claim => claim.Id == claimId)
            .Select(claim => claim.SlackSentAt != null)
            .SingleOrDefaultAsync(ct);

    public async Task MarkBudgetAlertSlackSentAsync(Guid claimId, Instant at, CancellationToken ct = default)
    {
        await ctx
            .BudgetAlertClaims.Where(claim => claim.Id == claimId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(claim => claim.SlackSentAt, at), ct);
    }
}
