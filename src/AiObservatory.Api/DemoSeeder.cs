using AiObservatory.Data;
using AiObservatory.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace AiObservatory.Api;

/// <summary>
/// The synthetic <c>demo-seed</c> dataset: what the local Compose quick start and the public
/// demo instance show. <see cref="HasAnyDataAsync"/> and <see cref="ClearAsync"/> share one
/// table list, so the empty-database guard and the reset cannot drift apart.
/// </summary>
internal static class DemoSeeder
{
    // Arbitrary constant; serialises overlapping resets (nightly schedule vs manual dispatch).
    private const long ResetLockKey = 7_272_700_001;

    public static async Task<bool> HasAnyDataAsync(AiObservatoryDbContext db, CancellationToken ct) =>
        await db.DailyAggregates.AnyAsync(ct)
        || await db.Subscriptions.AnyAsync(ct)
        || await db.Insights.AnyAsync(ct)
        || await db.BudgetRules.AnyAsync(ct)
        || await db.UsageEvents.AnyAsync(ct)
        || await db.SpendEntries.AnyAsync(ct);

    /// <summary>
    /// Deletes every row in the tables <see cref="SeedAsync"/> writes. The vendor and category
    /// catalog is migration data and is deliberately left alone. Only call this where the data
    /// is disposable: the demo reset does, and the dev seed route never does.
    /// </summary>
    public static async Task ClearAsync(AiObservatoryDbContext db, CancellationToken ct)
    {
        await db.DailyAggregates.ExecuteDeleteAsync(ct);
        await db.Subscriptions.ExecuteDeleteAsync(ct);
        await db.Insights.ExecuteDeleteAsync(ct);
        await db.BudgetRules.ExecuteDeleteAsync(ct);
        await db.UsageEvents.ExecuteDeleteAsync(ct);
        await db.SpendEntries.ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// Wipes and reseeds atomically, so a visitor never sees an empty dashboard and a failed
    /// seed rolls the wipe back. The advisory lock makes an overlapping second reset wait for
    /// the first instead of interleaving deletes and inserts.
    /// </summary>
    public static async Task ResetAsync(AiObservatoryDbContext db, IClock clock, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({ResetLockKey})", ct);
        await ClearAsync(db, ct);
        await SeedAsync(db, clock, ct);
        await transaction.CommitAsync(ct);
    }

    public static async Task SeedAsync(AiObservatoryDbContext db, IClock clock, CancellationToken ct)
    {
        var today = clock.GetCurrentInstant().InUtc().Date;

        for (int i = 0; i < 14; i++)
        {
            var date = today.PlusDays(-i);

            db.DailyAggregates.Add(
                new DailyAggregate
                {
                    Date = date,
                    Provider = Provider.Anthropic,
                    Model = "claude-3-5-sonnet",
                    SourceId = UsageSourceIds.DemoSeed,
                    SourceKind = SourceKind.Synthetic,
                    UsageScope = UsageScope.Api,
                    CostBasis = CostBasis.ListPriceEstimate,
                    InputTokens = 150000 + i * 1000,
                    OutputTokens = 80000 + i * 500,
                    CacheReadTokens = 30000 + i * 200,
                    CacheWriteTokens = 12000 + i * 100,
                    CostUsd = 2.50m + i * 0.15m,
                    RequestCount = 50 + i,
                }
            );

            db.DailyAggregates.Add(
                new DailyAggregate
                {
                    Date = date,
                    Provider = Provider.Anthropic,
                    Model = "claude-3-5-haiku",
                    SourceId = UsageSourceIds.DemoSeed,
                    SourceKind = SourceKind.Synthetic,
                    UsageScope = UsageScope.Api,
                    CostBasis = CostBasis.ListPriceEstimate,
                    InputTokens = 300000 + i * 2000,
                    OutputTokens = 150000 + i * 1000,
                    CacheReadTokens = 80000 + i * 500,
                    CacheWriteTokens = 25000 + i * 150,
                    CostUsd = 0.80m + i * 0.05m,
                    RequestCount = 120 + i,
                }
            );

            db.DailyAggregates.Add(
                new DailyAggregate
                {
                    Date = date,
                    Provider = Provider.Google,
                    Model = "gemini-1.5-pro",
                    SourceId = UsageSourceIds.DemoSeed,
                    SourceKind = SourceKind.Synthetic,
                    UsageScope = UsageScope.Api,
                    CostBasis = CostBasis.ListPriceEstimate,
                    InputTokens = 80000 + i * 500,
                    OutputTokens = 40000 + i * 200,
                    CacheReadTokens = 15000 + i * 100,
                    CacheWriteTokens = 5000 + i * 50,
                    CostUsd = 1.20m + i * 0.08m,
                    RequestCount = 30 + i,
                }
            );

            db.DailyAggregates.Add(
                new DailyAggregate
                {
                    Date = date,
                    Provider = Provider.Google,
                    Model = "gemini-1.5-flash",
                    SourceId = UsageSourceIds.DemoSeed,
                    SourceKind = SourceKind.Synthetic,
                    UsageScope = UsageScope.Api,
                    CostBasis = CostBasis.ListPriceEstimate,
                    InputTokens = 500000 + i * 5000,
                    OutputTokens = 250000 + i * 2000,
                    CacheReadTokens = 120000 + i * 1000,
                    CacheWriteTokens = 45000 + i * 400,
                    CostUsd = 0.40m + i * 0.02m,
                    RequestCount = 200 + i,
                }
            );

            db.DailyAggregates.Add(
                new DailyAggregate
                {
                    Date = date,
                    Provider = Provider.Copilot,
                    Model = "copilot-chat",
                    SourceId = UsageSourceIds.DemoSeed,
                    SourceKind = SourceKind.Synthetic,
                    UsageScope = UsageScope.Subscription,
                    CostBasis = CostBasis.Notional,
                    InputTokens = 40000 + i * 200,
                    OutputTokens = 20000 + i * 100,
                    CacheReadTokens = 0,
                    CacheWriteTokens = 0,
                    CostUsd = 0.30m + i * 0.01m,
                    RequestCount = 15 + i,
                }
            );
        }

        db.Subscriptions.Add(
            new Subscription
            {
                Provider = Provider.Copilot,
                Name = "GitHub Copilot Business",
                CostAmount = 19.00m,
                Currency = "USD",
                BillingDay = 1,
                ActiveFrom = today.PlusDays(-60),
                ActiveTo = null,
            }
        );

        db.Subscriptions.Add(
            new Subscription
            {
                Provider = Provider.Anthropic,
                Name = "Claude Pro",
                CostAmount = 18.00m,
                Currency = "GBP",
                BillingDay = 15,
                ActiveFrom = today.PlusDays(-30),
                ActiveTo = null,
                ExtraUsageCost = 5.50m,
            }
        );

        db.BudgetRules.Add(
            new BudgetRule
            {
                Provider = null,
                Period = BillingPeriod.Daily,
                ThresholdGbp = 5.00m,
                EvaluationStartsOn = today,
            }
        );

        db.BudgetRules.Add(
            new BudgetRule
            {
                Provider = Provider.Anthropic,
                Period = BillingPeriod.Monthly,
                ThresholdGbp = 150.00m,
                EvaluationStartsOn = today,
            }
        );

        db.Insights.Add(
            new Insight
            {
                GeneratedAt = clock.GetCurrentInstant(),
                PeriodStart = today.PlusDays(-7),
                PeriodEnd = today,
                InsightType = InsightType.Anomaly,
                Title = "Spend Spike on Claude 3.5 Sonnet",
                Body =
                    "Your Anthropic API cost spiked by 45% yesterday compared to the previous 7-day average. This was driven by a large batch code generation task.",
                Data = "{\"spikePercent\":45}",
            }
        );

        db.Insights.Add(
            new Insight
            {
                GeneratedAt = clock.GetCurrentInstant().Minus(Duration.FromHours(2)),
                PeriodStart = today.PlusDays(-7),
                PeriodEnd = today,
                InsightType = InsightType.Efficiency,
                Title = "Gemini Flash Cache Hits High",
                Body =
                    "Google Gemini 1.5 Flash query cache hit rate reached 82%, saving approximately $12.40 in input token costs over the past 3 days.",
                Data = "{\"savingsUsd\":12.4}",
            }
        );

        db.Insights.Add(
            new Insight
            {
                GeneratedAt = clock.GetCurrentInstant().Minus(Duration.FromHours(5)),
                PeriodStart = today.PlusDays(-7),
                PeriodEnd = today,
                InsightType = InsightType.Recommendation,
                Title = "Switch simple chat completions to Haiku",
                Body =
                    "43% of your Claude 3.5 Sonnet requests contain prompts under 200 tokens with low complexity. Switching these to Claude 3.5 Haiku could reduce your Anthropic spend by $15.50/month.",
                Data = "{\"potentialSavingsUsd\":15.5}",
            }
        );

        await SeedBilledLedgerAsync(db, today, clock.GetCurrentInstant(), ct);

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Vendor keys, and the monthly GBP charge, used to seed billed evidence in Development.
    /// Keys match the <c>SpendVendors</c> catalog migrations; a key absent from the catalog is
    /// skipped rather than failing the seed.
    /// </summary>
    private static readonly (string VendorKey, decimal MonthlyGbp)[] SeedBilledMonthlyGbp =
    [
        ("anthropic", 18.00m),
        ("coderabbit", 12.00m),
        ("github-actions", 7.50m),
    ];

    private static readonly string[] SeedBilledVendorKeys = [.. SeedBilledMonthlyGbp.Select(v => v.VendorKey)];

    /// <summary>
    /// Seeds billed evidence for the Reporting tab, which reads the spend ledger rather than
    /// <c>DailyAggregates</c>. Without it every Reporting tile reads "Not reported" on a fresh
    /// install and the tab cannot show what it is for.
    /// <para>
    /// Vendors are resolved by their stable catalog key rather than by hardcoded GUID, and a
    /// vendor that is missing — or that carries no default category — is skipped rather than
    /// failing the whole seed.
    /// </para>
    /// </summary>
    private static async Task SeedBilledLedgerAsync(
        AiObservatoryDbContext db,
        LocalDate today,
        Instant seededAt,
        CancellationToken ct
    )
    {
        var vendors = await db
            .SpendVendors.Where(v => SeedBilledVendorKeys.Contains(v.Key))
            .Select(v => new
            {
                v.Key,
                v.Id,
                v.DefaultCategoryId,
            })
            .ToListAsync(ct);

        var firstOfThisMonth = today.PlusDays(1 - today.Day);

        foreach (var (vendorKey, monthlyGbp) in SeedBilledMonthlyGbp)
        {
            var vendor = vendors.Find(v => v.Key == vendorKey);
            if (vendor?.DefaultCategoryId is not { } categoryId)
            {
                continue;
            }

            // One charge per month over three months so the Reporting comparison
            // ("previous period") has billed evidence on both sides of the boundary.
            for (int monthsBack = 0; monthsBack < 3; monthsBack++)
            {
                var occurredOn = firstOfThisMonth.PlusMonths(-monthsBack);
                db.SpendEntries.Add(
                    new SpendEntry
                    {
                        OccurredOn = occurredOn,
                        VendorId = vendor.Id,
                        CategoryId = categoryId,
                        Amount = monthlyGbp,
                        Currency = "GBP",
                        AmountGbp = monthlyGbp,
                        FxRate = 1m,
                        Description = "Demo seed — synthetic billed charge",
                        Source = SpendSource.Api,
                        EntryKey = $"demo-seed:{vendorKey}:{occurredOn.Year:D4}-{occurredOn.Month:D2}",
                        RecordedAt = seededAt,
                        SourceId = UsageSourceIds.DemoSeed,
                        SourceKind = SourceKind.Synthetic,
                        UsageScope = UsageScope.Subscription,
                        CostBasis = CostBasis.Billed,
                        ObservedAt = seededAt,
                    }
                );
            }
        }
    }
}
