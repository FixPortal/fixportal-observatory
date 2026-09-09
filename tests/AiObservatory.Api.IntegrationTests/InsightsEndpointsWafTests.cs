using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace AiObservatory.Api.IntegrationTests;

[Trait("Category", "Integration")]
public class InsightsEndpointsWafTests(AiObservatoryApiFactory factory) : IClassFixture<AiObservatoryApiFactory>
{
    [Fact]
    public async Task DeleteInsights_removes_claimed_and_unclaimed_insights_with_their_delivery_state()
    {
        var ct = TestContext.Current.CancellationToken;
        var period = new LocalDate(2026, 8, 25);
        var generatedAt = Instant.FromUtc(2026, 8, 26, 0, 5);
        var rule = new BudgetRule
        {
            Period = BillingPeriod.Daily,
            ThresholdGbp = 10m,
            EvaluationStartsOn = period,
        };
        var claimedInsight = Insight(period, generatedAt, "Claimed");
        var unclaimedInsight = Insight(period, generatedAt.Plus(Duration.FromMinutes(1)), "Unclaimed");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            db.AddRange(rule, claimedInsight, unclaimedInsight);
            db.BudgetAlertClaims.Add(
                new BudgetAlertClaim
                {
                    BudgetRuleId = rule.Id,
                    PeriodStart = period,
                    PeriodEnd = period,
                    InsightId = claimedInsight.Id,
                    ThresholdGbp = 10m,
                    ActualSpendGbp = 15m,
                    CreatedAt = generatedAt,
                    EmailLeaseId = Guid.NewGuid(),
                    EmailLeaseAcquiredAt = generatedAt,
                }
            );
            await db.SaveChangesAsync(ct);
        }

        using var client = factory.CreateAdminClient();
        var response = await client.DeleteAsync("/api/insights", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("deleted").GetInt32().Should().Be(2);
        using var assertionScope = factory.Services.CreateScope();
        var assertionDb = assertionScope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        (await assertionDb.Insights.AsNoTracking().CountAsync(ct)).Should().Be(0);
        (await assertionDb.BudgetAlertClaims.AsNoTracking().CountAsync(ct)).Should().Be(0);
    }

    [Fact]
    public async Task DeleteInsights_returns_conflict_and_preserves_rows_when_an_email_lease_is_fresh()
    {
        var ct = TestContext.Current.CancellationToken;
        var period = new LocalDate(2026, 8, 25);
        var rule = new BudgetRule
        {
            Period = BillingPeriod.Daily,
            ThresholdGbp = 10m,
            EvaluationStartsOn = period,
        };
        var insight = Insight(period, Instant.FromUtc(2026, 8, 26, 0, 5), "Fresh lease");
        var claim = new BudgetAlertClaim
        {
            BudgetRuleId = rule.Id,
            PeriodStart = period,
            PeriodEnd = period,
            InsightId = insight.Id,
            ThresholdGbp = 10m,
            ActualSpendGbp = 15m,
            CreatedAt = insight.GeneratedAt,
            EmailLeaseId = Guid.NewGuid(),
        };

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            claim.EmailLeaseAcquiredAt = scope.ServiceProvider.GetRequiredService<IClock>().GetCurrentInstant();
            db.AddRange(rule, insight, claim);
            await db.SaveChangesAsync(ct);
        }

        try
        {
            using var client = factory.CreateAdminClient();
            var response = await client.DeleteAsync("/api/insights", ct);

            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            using var assertionScope = factory.Services.CreateScope();
            var assertionDb = assertionScope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            (await assertionDb.Insights.AsNoTracking().CountAsync(candidate => candidate.Id == insight.Id, ct))
                .Should()
                .Be(1);
            (await assertionDb.BudgetAlertClaims.AsNoTracking().CountAsync(candidate => candidate.Id == claim.Id, ct))
                .Should()
                .Be(1);
        }
        finally
        {
            await CleanupAsync(rule.Id, insight.Id, CancellationToken.None);
        }
    }

    [Fact]
    public async Task DeleteInsights_returns_conflict_when_email_lease_acquisition_wins_the_race()
    {
        var ct = TestContext.Current.CancellationToken;
        var period = new LocalDate(2026, 8, 25);
        var rule = new BudgetRule
        {
            Period = BillingPeriod.Daily,
            ThresholdGbp = 10m,
            EvaluationStartsOn = period,
        };
        var insight = Insight(period, Instant.FromUtc(2026, 8, 26, 0, 5), "Racing lease");
        var claim = new BudgetAlertClaim
        {
            BudgetRuleId = rule.Id,
            PeriodStart = period,
            PeriodEnd = period,
            InsightId = insight.Id,
            ThresholdGbp = 10m,
            ActualSpendGbp = 15m,
            CreatedAt = insight.GeneratedAt,
        };
        Instant acquiredAt;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            acquiredAt = scope.ServiceProvider.GetRequiredService<IClock>().GetCurrentInstant();
            db.AddRange(rule, insight, claim);
            await db.SaveChangesAsync(ct);
        }

        try
        {
            using var leaseScope = factory.Services.CreateScope();
            var leaseDb = leaseScope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            await using var leaseTx = await leaseDb.Database.BeginTransactionAsync(ct);
            var repository = new UsageRepository(leaseDb);
            var acquired = await repository.TryAcquireBudgetAlertEmailLeaseAsync(
                claim.Id,
                Guid.NewGuid(),
                acquiredAt,
                acquiredAt.Minus(Duration.FromMinutes(15)),
                ct
            );
            acquired.Should().BeTrue();

            using var client = factory.CreateAdminClient();
            var deleteTask = client.DeleteAsync("/api/insights", ct);
            // Deterministic negative: the purge must be genuinely blocked on the uncommitted
            // lease write (its LOCK TABLE is in a PostgreSQL lock wait) before the lease
            // commits — a fixed delay would pass on a slow machine without the purge ever
            // contending.
            await WaitForBlockedStatementAsync("LOCK TABLE \"BudgetAlertClaims\"", ct);

            await leaseTx.CommitAsync(ct);
            var response = await deleteTask;

            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            using var assertionScope = factory.Services.CreateScope();
            var assertionDb = assertionScope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            (await assertionDb.Insights.AsNoTracking().CountAsync(candidate => candidate.Id == insight.Id, ct))
                .Should()
                .Be(1);
            (await assertionDb.BudgetAlertClaims.AsNoTracking().CountAsync(candidate => candidate.Id == claim.Id, ct))
                .Should()
                .Be(1);
        }
        finally
        {
            await CleanupAsync(rule.Id, insight.Id, CancellationToken.None);
        }
    }

    [Fact]
    public async Task DeleteInsights_fences_email_lease_acquisition_until_a_successful_purge_commits()
    {
        var ct = TestContext.Current.CancellationToken;
        var period = new LocalDate(2026, 8, 25);
        var rule = new BudgetRule
        {
            Period = BillingPeriod.Daily,
            ThresholdGbp = 10m,
            EvaluationStartsOn = period,
        };
        var insight = Insight(period, Instant.FromUtc(2026, 8, 26, 0, 5), "Purge wins");
        var claim = new BudgetAlertClaim
        {
            BudgetRuleId = rule.Id,
            PeriodStart = period,
            PeriodEnd = period,
            InsightId = insight.Id,
            ThresholdGbp = 10m,
            ActualSpendGbp = 15m,
            CreatedAt = insight.GeneratedAt,
        };
        Instant acquiredAt;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            acquiredAt = scope.ServiceProvider.GetRequiredService<IClock>().GetCurrentInstant();
            db.AddRange(rule, insight, claim);
            await db.SaveChangesAsync(ct);
        }

        try
        {
            using var blockerScope = factory.Services.CreateScope();
            var blockerDb = blockerScope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            await using var blockerTx = await blockerDb.Database.BeginTransactionAsync(ct);
            await blockerDb
                .Insights.FromSqlInterpolated($"""SELECT * FROM "Insights" WHERE "Id" = {insight.Id} FOR UPDATE""")
                .AsNoTracking()
                .SingleAsync(ct);

            using var client = factory.CreateAdminClient();
            var deleteTask = client.DeleteAsync("/api/insights", ct);
            await WaitForBlockedInsightDeleteAsync(ct);

            using var acquisitionScope = factory.Services.CreateScope();
            var acquisitionDb = acquisitionScope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            var repository = new UsageRepository(acquisitionDb);
            var acquisitionTask = repository.TryAcquireBudgetAlertEmailLeaseAsync(
                claim.Id,
                Guid.NewGuid(),
                acquiredAt,
                acquiredAt.Minus(Duration.FromMinutes(15)),
                ct
            );
            // Deterministic negative: the acquisition's UPDATE must be genuinely queued behind
            // the purge's SHARE ROW EXCLUSIVE table lock before the blocker commits — a fixed
            // delay would pass vacuously on a slow machine.
            await WaitForBlockedStatementAsync("UPDATE \"BudgetAlertClaims\"", ct);

            await blockerTx.CommitAsync(ct);
            var response = await deleteTask;

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await acquisitionTask).Should().BeFalse();
            using var assertionScope = factory.Services.CreateScope();
            var assertionDb = assertionScope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            (await assertionDb.Insights.AsNoTracking().CountAsync(candidate => candidate.Id == insight.Id, ct))
                .Should()
                .Be(0);
            (await assertionDb.BudgetAlertClaims.AsNoTracking().CountAsync(candidate => candidate.Id == claim.Id, ct))
                .Should()
                .Be(0);
        }
        finally
        {
            await CleanupAsync(rule.Id, insight.Id, CancellationToken.None);
        }
    }

    [Fact]
    public async Task DeleteInsights_rolls_back_claim_deletion_when_insight_deletion_fails()
    {
        var ct = TestContext.Current.CancellationToken;
        var period = new LocalDate(2026, 8, 24);
        var generatedAt = Instant.FromUtc(2026, 8, 25, 0, 5);
        var rule = new BudgetRule
        {
            Period = BillingPeriod.Daily,
            ThresholdGbp = 10m,
            EvaluationStartsOn = period,
        };
        var insight = Insight(period, generatedAt, "Rollback");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            db.AddRange(rule, insight);
            db.BudgetAlertClaims.Add(
                new BudgetAlertClaim
                {
                    BudgetRuleId = rule.Id,
                    PeriodStart = period,
                    PeriodEnd = period,
                    InsightId = insight.Id,
                    ThresholdGbp = 10m,
                    ActualSpendGbp = 15m,
                    CreatedAt = generatedAt,
                }
            );
            await db.SaveChangesAsync(ct);
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE FUNCTION reject_insight_delete() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'forced insight-delete failure';
                END
                $$;
                CREATE TRIGGER reject_insight_delete
                    BEFORE DELETE ON "Insights"
                    FOR EACH STATEMENT EXECUTE FUNCTION reject_insight_delete();
                """,
                ct
            );
        }

        try
        {
            using var client = factory.CreateAdminClient();
            var response = await client.DeleteAsync("/api/insights", ct);

            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            using var assertionScope = factory.Services.CreateScope();
            var assertionDb = assertionScope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            (await assertionDb.Insights.AsNoTracking().CountAsync(ct)).Should().Be(1);
            (await assertionDb.BudgetAlertClaims.AsNoTracking().CountAsync(ct)).Should().Be(1);
        }
        finally
        {
            using var cleanupScope = factory.Services.CreateScope();
            var cleanupDb = cleanupScope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            await cleanupDb.Database.ExecuteSqlRawAsync(
                """
                DROP TRIGGER IF EXISTS reject_insight_delete ON "Insights";
                DROP FUNCTION IF EXISTS reject_insight_delete();
                """,
                CancellationToken.None
            );
            await cleanupDb
                .BudgetAlertClaims.Where(claim => claim.InsightId == insight.Id)
                .ExecuteDeleteAsync(CancellationToken.None);
            await cleanupDb
                .Insights.Where(candidate => candidate.Id == insight.Id)
                .ExecuteDeleteAsync(CancellationToken.None);
            await cleanupDb
                .BudgetRules.Where(candidate => candidate.Id == rule.Id)
                .ExecuteDeleteAsync(CancellationToken.None);
        }
    }

    private static Insight Insight(LocalDate period, Instant generatedAt, string title) =>
        new()
        {
            GeneratedAt = generatedAt,
            PeriodStart = period,
            PeriodEnd = period,
            InsightType = InsightType.BudgetAlert,
            Title = title,
            Body = title,
        };

    private async Task CleanupAsync(Guid ruleId, Guid insightId, CancellationToken ct)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        await db.BudgetAlertClaims.Where(claim => claim.InsightId == insightId).ExecuteDeleteAsync(ct);
        await db.Insights.Where(insight => insight.Id == insightId).ExecuteDeleteAsync(ct);
        await db.BudgetRules.Where(rule => rule.Id == ruleId).ExecuteDeleteAsync(ct);
    }

    private async Task WaitForBlockedInsightDeleteAsync(CancellationToken ct) =>
        await WaitForBlockedStatementAsync("DELETE FROM \"Insights\"", ct);

    /// <summary>
    /// Waits until another session is in a PostgreSQL heavyweight-lock wait while executing a
    /// statement whose text contains <paramref name="statementFragment"/> — the deterministic
    /// replacement for "delay N ms and assert the task had not finished". The fragment must be
    /// a literal (no LIKE wildcards, no apostrophes).
    /// </summary>
    private async Task WaitForBlockedStatementAsync(string statementFragment, CancellationToken ct)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var blocked = await db
                .Database.SqlQuery<bool>(
                    $"""
                    SELECT EXISTS (
                        SELECT 1
                        FROM pg_stat_activity
                        WHERE datname = current_database()
                            AND pid <> pg_backend_pid()
                            AND wait_event_type = 'Lock'
                            AND query LIKE '%' || {statementFragment} || '%'
                    ) AS "Value"
                    """
                )
                .SingleAsync(ct);
            if (blocked)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
        }

        throw new TimeoutException($"No session reached the expected PostgreSQL lock wait for '{statementFragment}'.");
    }
}
