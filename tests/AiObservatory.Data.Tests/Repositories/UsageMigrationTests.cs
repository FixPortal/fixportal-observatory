using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;
using Npgsql;

namespace AiObservatory.Data.Tests.Repositories;

[Trait("Category", "Integration")]
public class UsageMigrationTests : IAsyncLifetime
{
    private string _connectionString = null!;
    private DbContextOptions<AiObservatoryDbContext> _options = null!;

    public ValueTask InitializeAsync()
    {
        var baseConnection =
            Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
            ?? "Host=localhost;Database=aiobs_test;Username=postgres;Password=postgres";
        _connectionString = new NpgsqlConnectionStringBuilder(baseConnection)
        {
            Database = $"aiobs_test_migration_{Guid.NewGuid():N}",
        }.ConnectionString;
        _options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(_connectionString, options => options.UseNodaTime())
            .Options;
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await using var db = new AiObservatoryDbContext(_options);
        await db.Database.EnsureDeletedAsync();
    }

    [Theory]
    [InlineData(SubscriptionBillingInterval.Annual, 13)]
    [InlineData(SubscriptionBillingInterval.Annual, null)]
    [InlineData(SubscriptionBillingInterval.Monthly, 7)]
    public async Task SubscriptionBillingMigration_RejectsInvalidIntervalMonthPairs(
        SubscriptionBillingInterval interval,
        int? billingMonth
    )
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = new AiObservatoryDbContext(_options);
        await db.Database.MigrateAsync(ct);
        db.Subscriptions.Add(
            new Subscription
            {
                Provider = Provider.Google,
                Name = "Invalid subscription",
                CostAmount = 1m,
                Currency = "GBP",
                BillingInterval = interval,
                BillingMonth = billingMonth,
                BillingDay = 1,
                ActiveFrom = new LocalDate(2026, 1, 1),
            }
        );

        var save = () => db.SaveChangesAsync(ct);

        await save.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task AddUnknownCostCoverage_BackfillsGroupedLegacyNullCostsAndAllowsKnownZeroCorrection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var beforeCoverage = new AiObservatoryDbContext(_options))
        {
            var migrator = beforeCoverage.Database.GetService<IMigrator>();
            await migrator.MigrateAsync("20260812022935_AddUsageEventTelemetryIdentity", ct);
            const string rawPayload = "{}";
            await beforeCoverage.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "UsageEvents" ("Id", "Provider", "OccurredAt", "IngestedAt", "Model", "InputTokens", "OutputTokens", "CostUsd", "RawPayload", "EventKey")
                VALUES ('10000000-0000-0000-0000-000000000001', 'OpenAI', '2026-08-12T00:30:00Z', '2026-08-12T00:30:00Z', NULL, 1, 1, NULL, CAST({rawPayload} AS jsonb), 'legacy-null-cost-a')
                """,
                ct
            );
            await beforeCoverage.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "UsageEvents" ("Id", "Provider", "OccurredAt", "IngestedAt", "Model", "InputTokens", "OutputTokens", "CostUsd", "RawPayload", "EventKey")
                VALUES ('10000000-0000-0000-0000-000000000002', 'OpenAI', '2026-08-12T23:30:00Z', '2026-08-12T23:30:00Z', NULL, 1, 1, NULL, CAST({rawPayload} AS jsonb), 'legacy-null-cost-b')
                """,
                ct
            );
            await beforeCoverage.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "UsageEvents" ("Id", "Provider", "OccurredAt", "IngestedAt", "Model", "InputTokens", "OutputTokens", "CostUsd", "RawPayload", "EventKey")
                VALUES ('10000000-0000-0000-0000-000000000003', 'Google', '2026-08-13T00:30:00Z', '2026-08-13T00:30:00Z', 'control', 1, 1, NULL, CAST({rawPayload} AS jsonb), 'legacy-null-cost-control')
                """,
                ct
            );
            await beforeCoverage.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "DailyAggregates" ("Date", "Provider", "Model", "InputTokens", "OutputTokens", "CacheReadTokens", "CacheWriteTokens", "CacheWrite1hTokens", "CostUsd", "RequestCount")
                VALUES ('2026-08-12', 'OpenAI', 'unknown', 2, 2, 0, 0, 0, 0, 2)
                """,
                ct
            );
            await beforeCoverage.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "DailyAggregates" ("Date", "Provider", "Model", "InputTokens", "OutputTokens", "CacheReadTokens", "CacheWriteTokens", "CacheWrite1hTokens", "CostUsd", "RequestCount")
                VALUES ('2026-08-13', 'Google', 'control', 1, 1, 0, 0, 0, 0, 1)
                """,
                ct
            );

            await migrator.MigrateAsync(cancellationToken: ct);
        }

        await using var afterCoverage = new AiObservatoryDbContext(_options);
        var aggregates = await afterCoverage.DailyAggregates.ToListAsync(ct);
        var usage = await afterCoverage
            .UsageEvents.AsNoTracking()
            .SingleAsync(e => e.EventKey == "OpenAI:legacy-null-cost-a", ct);
        usage.SourceId.Should().Be(UsageSourceIds.LegacyApi);
        usage.SourceKind.Should().Be(SourceKind.Legacy);
        usage.UsageScope.Should().Be(UsageScope.Unknown);
        usage.CostBasis.Should().Be(CostBasis.Unknown);
        usage.ObservedAt.Should().Be(usage.IngestedAt);

        var aggregate = await afterCoverage
            .DailyAggregates.AsNoTracking()
            .SingleAsync(a => a.Provider == Provider.OpenAI && a.Model == "unknown", ct);
        aggregate.SourceId.Should().Be(UsageSourceIds.LegacyApi);
        aggregate.SourceKind.Should().Be(SourceKind.Legacy);
        aggregate.UsageScope.Should().Be(UsageScope.Unknown);
        aggregate.CostBasis.Should().Be(CostBasis.Unknown);

        aggregates
            .Should()
            .ContainSingle(a => a.Provider == Provider.OpenAI && a.Model == "unknown")
            .Which.UnknownCostCount.Should()
            .Be(2);
        aggregates
            .Should()
            .ContainSingle(a => a.Provider == Provider.Google && a.Model == "control")
            .Which.UnknownCostCount.Should()
            .Be(1);

        var repository = new UsageRepository(afterCoverage);
        var patch = await repository.PatchEventCostAsync(
            Provider.OpenAI,
            UsageSourceIds.LegacyApi,
            "legacy-null-cost-a",
            0m,
            ct: TestContext.Current.CancellationToken
        );

        patch.Should().NotBeNull();
        aggregates = await afterCoverage.DailyAggregates.AsNoTracking().ToListAsync(ct);
        aggregates
            .Should()
            .ContainSingle(a => a.Provider == Provider.OpenAI && a.Model == "unknown")
            .Which.UnknownCostCount.Should()
            .Be(1);
        aggregates
            .Should()
            .ContainSingle(a => a.Provider == Provider.Google && a.Model == "control")
            .Which.UnknownCostCount.Should()
            .Be(1);
        (await afterCoverage.UsageEvents.AsNoTracking().SingleAsync(e => e.EventKey == "OpenAI:legacy-null-cost-a", ct))
            .CostUsd.Should()
            .Be(0m);
    }

    [Fact]
    public async Task AddObservationProvenance_preserves_same_legacy_key_from_different_providers()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var beforeProvenance = new AiObservatoryDbContext(_options))
        {
            var migrator = beforeProvenance.Database.GetService<IMigrator>();
            await migrator.MigrateAsync("20260812024132_AddUnknownCostCoverage", ct);
            const string rawPayload = "{}";
            const string legacyKey = "x";
            const string prefixedLegacyKey = "OpenAI:x";
            await beforeProvenance.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "UsageEvents" ("Id", "Provider", "OccurredAt", "IngestedAt", "Model", "InputTokens", "OutputTokens", "CostUsd", "RawPayload", "EventKey")
                VALUES ('20000000-0000-0000-0000-000000000001', 'OpenAI', '2026-08-12T00:30:00Z', '2026-08-12T00:30:00Z', 'gpt-5.4', 1, 1, 1, CAST({rawPayload} AS jsonb), {legacyKey})
                """,
                ct
            );
            await beforeProvenance.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "UsageEvents" ("Id", "Provider", "OccurredAt", "IngestedAt", "Model", "InputTokens", "OutputTokens", "CostUsd", "RawPayload", "EventKey")
                VALUES ('20000000-0000-0000-0000-000000000002', 'OpenAI', '2026-08-12T00:30:00Z', '2026-08-12T00:30:00Z', 'gpt-5.4', 1, 1, 1, CAST({rawPayload} AS jsonb), {prefixedLegacyKey})
                """,
                ct
            );
            await beforeProvenance.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "UsageEvents" ("Id", "Provider", "OccurredAt", "IngestedAt", "Model", "InputTokens", "OutputTokens", "CostUsd", "RawPayload", "EventKey")
                VALUES ('20000000-0000-0000-0000-000000000003', 'Google', '2026-08-12T00:30:00Z', '2026-08-12T00:30:00Z', 'gemini-2.5-pro', 1, 1, 1, CAST({rawPayload} AS jsonb), {legacyKey})
                """,
                ct
            );
            await beforeProvenance.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "UsageEvents" ("Id", "Provider", "OccurredAt", "IngestedAt", "Model", "InputTokens", "OutputTokens", "CostUsd", "RawPayload", "EventKey")
                VALUES ('20000000-0000-0000-0000-000000000004', 'Google', '2026-08-12T00:30:00Z', '2026-08-12T00:30:00Z', 'gemini-2.5-pro', 1, 1, 1, CAST({rawPayload} AS jsonb), {prefixedLegacyKey})
                """,
                ct
            );

            await migrator.MigrateAsync("20260824172007_AddObservationProvenance", ct);
        }

        await using var afterProvenance = new AiObservatoryDbContext(_options);
        // Raw SQL, not the EF model: the database sits at AddObservationProvenance while the
        // current entity has later columns (e.g. CorrectedAt), which the model would select and
        // fail on. The rollback leg below already reads this way for the same reason.
        var keys = await afterProvenance
            .Database.SqlQueryRaw<string>("SELECT \"EventKey\" AS \"Value\" FROM \"UsageEvents\" ORDER BY \"Provider\"")
            .ToListAsync(ct);

        keys.Should().HaveCount(4);
        keys.Should().BeEquivalentTo("OpenAI:x", "OpenAI:OpenAI:x", "Google:x", "Google:OpenAI:x");

        var rollbackMigrator = afterProvenance.Database.GetService<IMigrator>();
        await rollbackMigrator.MigrateAsync("20260812024132_AddUnknownCostCoverage", ct);
        var restoredKeys = await afterProvenance
            .Database.SqlQueryRaw<string>("SELECT \"EventKey\" AS \"Value\" FROM \"UsageEvents\" ORDER BY \"Provider\"")
            .ToListAsync(ct);

        restoredKeys.Should().BeEquivalentTo("x", "OpenAI:x", "x", "OpenAI:x");
    }

    [Fact]
    public async Task LatestMigration_RemovesOnlyLegacyGitHubRowsWithCanonicalCounterparts()
    {
        var ct = TestContext.Current.CancellationToken;
        var pairedLegacyId = Guid.Parse("40000000-0000-0000-0000-000000000001");
        var canonicalId = Guid.Parse("40000000-0000-0000-0000-000000000002");
        var unpairedLegacyId = Guid.Parse("40000000-0000-0000-0000-000000000003");
        var portalId = Guid.Parse("40000000-0000-0000-0000-000000000004");
        const string observationKey = "github:2026-08:actions:Actions Linux";
        const string rawPayload = "{}";

        await using (var beforeCleanup = new AiObservatoryDbContext(_options))
        {
            var migrator = beforeCleanup.Database.GetService<IMigrator>();
            await migrator.MigrateAsync("20260827152216_AddSubscriptionBillingInterval", ct);
            var vendorId = await beforeCleanup
                .SpendVendors.Where(vendor => vendor.Key == "github-actions")
                .Select(vendor => vendor.Id)
                .SingleAsync(ct);
            var categoryId = await beforeCleanup
                .SpendCategories.Where(category => category.Key == "ci")
                .Select(category => category.Id)
                .SingleAsync(ct);

            SpendEntry Row(
                Guid id,
                SpendSource source,
                string sourceId,
                string entryKey,
                decimal amount = 10m,
                SourceKind sourceKind = SourceKind.Legacy,
                UsageScope usageScope = UsageScope.Unknown
            ) =>
                new()
                {
                    Id = id,
                    OccurredOn = new LocalDate(2026, 8, 1),
                    VendorId = vendorId,
                    CategoryId = categoryId,
                    Amount = amount,
                    Currency = "GBP",
                    AmountGbp = amount,
                    FxRate = 1m,
                    Description = "Migration test",
                    Source = source,
                    EntryKey = entryKey,
                    RecordedAt = Instant.FromUtc(2026, 8, 24, 0, 0),
                    RawPayload = rawPayload,
                    SourceId = sourceId,
                    SourceKind = sourceKind,
                    UsageScope = usageScope,
                    CostBasis = CostBasis.Billed,
                    ObservedAt = Instant.FromUtc(2026, 8, 24, 0, 0),
                };

            beforeCleanup.SpendEntries.AddRange(
                Row(pairedLegacyId, SpendSource.Api, UsageSourceIds.LegacySpend, observationKey),
                Row(
                    canonicalId,
                    SpendSource.Api,
                    UsageSourceIds.GitHubBillingApi,
                    $"billing:{UsageSourceIds.GitHubBillingApi}:{observationKey}",
                    11m,
                    SourceKind.ProviderApi,
                    UsageScope.Mixed
                ),
                Row(unpairedLegacyId, SpendSource.Api, UsageSourceIds.LegacySpend, "github:2026-08:actions:Unpaired"),
                Row(portalId, SpendSource.Portal, UsageSourceIds.LegacySpend, "expense:1")
            );
            await beforeCleanup.SaveChangesAsync(ct);
            await migrator.MigrateAsync(cancellationToken: ct);
        }

        await using var afterCleanup = new AiObservatoryDbContext(_options);
        var survivingIds = await afterCleanup
            .SpendEntries.AsNoTracking()
            .Where(entry =>
                entry.Id == pairedLegacyId
                || entry.Id == canonicalId
                || entry.Id == unpairedLegacyId
                || entry.Id == portalId
            )
            .Select(entry => entry.Id)
            .ToListAsync(ct);

        survivingIds.Should().BeEquivalentTo([canonicalId, unpairedLegacyId, portalId]);
    }

    [Fact]
    public async Task BudgetThresholdRenameThenGbpConversion_is_a_two_step_deployment_boundary()
    {
        var ct = TestContext.Current.CancellationToken;
        var existingRuleId = Guid.Parse("30000000-0000-0000-0000-000000000001");
        var newRuleId = Guid.Parse("30000000-0000-0000-0000-000000000002");
        await using (var beforeMigration = new AiObservatoryDbContext(_options))
        {
            var migrator = beforeMigration.Database.GetService<IMigrator>();
            await migrator.MigrateAsync("20260825220510_TrackPendingSourceWindows", ct);
            await beforeMigration.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "BudgetRules" ("Id", "Period", "ThresholdUsd")
                VALUES ({existingRuleId}, 'Daily', 123.45)
                """,
                ct
            );

            // The rename itself is the value-preserving boundary: 123.45 USD becomes 123.45
            // "GBP" here, which is exactly the drift the follow-up migration then corrects.
            await migrator.MigrateAsync("20260826130157_AddBudgetAlertsAndRenameThresholdToGbp", ct);
            var renamed = await beforeMigration
                .Database.SqlQuery<decimal>(
                    $"""SELECT "ThresholdGbp" AS "Value" FROM "BudgetRules" WHERE "Id" = {existingRuleId}"""
                )
                .SingleAsync(ct);
            renamed.Should().Be(123.45m);

            await migrator.MigrateAsync(cancellationToken: ct);
        }

        await using var afterMigration = new AiObservatoryDbContext(_options);
        var databaseUtcDate = await afterMigration
            .Database.SqlQueryRaw<LocalDate>("SELECT (CURRENT_TIMESTAMP AT TIME ZONE 'UTC')::date AS \"Value\"")
            .SingleAsync(ct);
        var existingRule = await afterMigration
            .BudgetRules.AsNoTracking()
            .SingleAsync(rule => rule.Id == existingRuleId, ct);
        // Pinned to the ledger's documented USD->GBP fallback rate (FxRateProvider, 0.79).
        existingRule.ThresholdGbp.Should().Be(97.53m);
        existingRule.EvaluationStartsOn.Should().Be(databaseUtcDate);

        // The EvaluationStartsOn default is gone, so an insert that omits it fails rather
        // than silently acquiring "today"; writers set it explicitly.
        var omitStart = () =>
            afterMigration.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "BudgetRules" ("Id", "Period", "ThresholdGbp")
                VALUES ({newRuleId}, 'Daily', 10)
                """,
                ct
            );
        await omitStart.Should().ThrowAsync<PostgresException>();

        await afterMigration.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "BudgetRules" ("Id", "Period", "ThresholdGbp", "EvaluationStartsOn")
            VALUES ({newRuleId}, 'Daily', 10, {databaseUtcDate})
            """,
            ct
        );
        var newRule = await afterMigration.BudgetRules.AsNoTracking().SingleAsync(rule => rule.Id == newRuleId, ct);
        newRule.EvaluationStartsOn.Should().Be(databaseUtcDate);

        var claimConstraints = await afterMigration
            .Database.SqlQueryRaw<string>(
                """
                SELECT conname AS "Value"
                FROM pg_constraint
                WHERE conrelid = '"BudgetAlertClaims"'::regclass
                  AND conname IN ('CK_BudgetAlertClaim_EmailLease', 'CK_BudgetAlertClaim_Period')
                ORDER BY conname
                """
            )
            .ToListAsync(ct);
        claimConstraints.Should().BeEquivalentTo("CK_BudgetAlertClaim_EmailLease", "CK_BudgetAlertClaim_Period");

        // The GBP conversion is deliberately not reversed on rollback: re-dividing would
        // compound the rounding, and the pre-conversion USD figure is unrecoverable anyway.
        var rollbackMigrator = afterMigration.Database.GetService<IMigrator>();
        await rollbackMigrator.MigrateAsync("20260825220510_TrackPendingSourceWindows", ct);
        var restoredThreshold = await afterMigration
            .Database.SqlQuery<decimal>(
                $"""SELECT "ThresholdUsd" AS "Value" FROM "BudgetRules" WHERE "Id" = {existingRuleId}"""
            )
            .SingleAsync(ct);
        var removedObjects = await afterMigration
            .Database.SqlQueryRaw<string>(
                """
                SELECT table_name AS "Value"
                FROM information_schema.tables
                WHERE table_schema = 'public' AND table_name = 'BudgetAlertClaims'
                UNION ALL
                SELECT column_name AS "Value"
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name = 'BudgetRules'
                  AND column_name = 'EvaluationStartsOn'
                """
            )
            .ToListAsync(ct);

        restoredThreshold.Should().Be(97.53m);
        removedObjects.Should().BeEmpty();

        // The marker recorded by GuardBudgetThresholdGbpConversion survives the rollback
        // (its Down deliberately keeps it), so the re-applied conversion Up skips instead
        // of converting a second time: 97.53 stays 97.53 rather than shrinking to 77.05.
        var markers = await afterMigration
            .Database.SqlQueryRaw<string>("""SELECT "Name" AS "Value" FROM "DataMigrationMarkers" """)
            .ToListAsync(ct);
        markers.Should().Contain("BudgetThresholdsConvertedToGbp");

        await rollbackMigrator.MigrateAsync(cancellationToken: ct);
        var replayedThreshold = await afterMigration
            .Database.SqlQuery<decimal>(
                $"""SELECT "ThresholdGbp" AS "Value" FROM "BudgetRules" WHERE "Id" = {existingRuleId}"""
            )
            .SingleAsync(ct);
        replayedThreshold.Should().Be(97.53m);
    }

    [Fact]
    public async Task GuardBudgetThresholdGbpConversion_SurfacesRulesForOperatorReview()
    {
        var ct = TestContext.Current.CancellationToken;
        var ruleId = Guid.Parse("30000000-0000-0000-0000-000000000009");
        await using var db = new AiObservatoryDbContext(_options);
        var migrator = db.Database.GetService<IMigrator>();
        await migrator.MigrateAsync("20260826130157_AddBudgetAlertsAndRenameThresholdToGbp", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "BudgetRules" ("Id", "Period", "ThresholdGbp")
            VALUES ({ruleId}, 'Daily', 123.45)
            """,
            ct
        );

        var notices = new List<string>();
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        connection.Notice += (_, args) => notices.Add(args.Notice.MessageText);
        await migrator.MigrateAsync(cancellationToken: ct);

        // Rules created between the rename and the conversion cannot be told apart by a
        // predicate, so the guard migration lists every rule in the deploy log instead of
        // converting anything automatically.
        notices.Should().Contain(notice => notice.Contains("operator review") && notice.Contains(ruleId.ToString()));
        var convertedOnce = await db
            .Database.SqlQuery<decimal>(
                $"""SELECT "ThresholdGbp" AS "Value" FROM "BudgetRules" WHERE "Id" = {ruleId}"""
            )
            .SingleAsync(ct);
        convertedOnce.Should().Be(97.53m);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    public async Task EnforceSchemaGuardConstraints_RejectsOutOfRangeBillingDay(int billingDay)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = new AiObservatoryDbContext(_options);
        await db.Database.MigrateAsync(ct);
        db.Subscriptions.Add(
            new Subscription
            {
                Provider = Provider.Google,
                Name = "Invalid billing day",
                CostAmount = 1m,
                Currency = "GBP",
                BillingDay = billingDay,
                ActiveFrom = new LocalDate(2026, 1, 1),
            }
        );

        var save = () => db.SaveChangesAsync(ct);

        await save.Should().ThrowAsync<DbUpdateException>();
    }

    [Theory]
    [InlineData("usd")]
    [InlineData("US")]
    [InlineData(" USd")]
    public async Task EnforceSchemaGuardConstraints_RejectsUnnormalizedSpendEntryCurrency(string currency)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = new AiObservatoryDbContext(_options);
        await db.Database.MigrateAsync(ct);
        var vendorId = await db
            .SpendVendors.Where(vendor => vendor.Key == "github-actions")
            .Select(vendor => vendor.Id)
            .SingleAsync(ct);
        var categoryId = await db
            .SpendCategories.Where(category => category.Key == "ci")
            .Select(category => category.Id)
            .SingleAsync(ct);
        db.SpendEntries.Add(
            new SpendEntry
            {
                OccurredOn = new LocalDate(2026, 8, 1),
                VendorId = vendorId,
                CategoryId = categoryId,
                Amount = 1m,
                Currency = currency,
                AmountGbp = 1m,
                FxRate = 1m,
                Source = SpendSource.Portal,
                RecordedAt = Instant.FromUtc(2026, 8, 31, 0, 0),
                SourceId = UsageSourceIds.LegacySpend,
                SourceKind = SourceKind.Legacy,
                UsageScope = UsageScope.Unknown,
                CostBasis = CostBasis.Billed,
                ObservedAt = Instant.FromUtc(2026, 8, 31, 0, 0),
            }
        );

        var save = () => db.SaveChangesAsync(ct);

        await save.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task EnforceSchemaGuardConstraints_RejectsNegativeUnknownCacheSavingsCount()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = new AiObservatoryDbContext(_options);
        await db.Database.MigrateAsync(ct);

        // Negative CacheSavingsUsd is deliberately allowed (a cache write can cost more
        // than it saves); only the count is floored.
        var aggregate = new DailyAggregate
        {
            Date = new LocalDate(2026, 8, 31),
            Provider = Provider.OpenAI,
            Model = "gpt-5.4",
            UnknownCacheSavingsCount = -1,
        };
        db.DailyAggregates.Add(aggregate);

        var save = () => db.SaveChangesAsync(ct);

        await save.Should().ThrowAsync<DbUpdateException>();
    }

    public static IEnumerable<object[]> SchemaGuardViolationSeeds =>
        new[]
        {
            new object[]
            {
                """
                    INSERT INTO "Subscriptions" ("Id", "Provider", "Name", "CostAmount", "Currency", "BillingInterval", "BillingDay", "ActiveFrom")
                    VALUES ('60000000-0000-0000-0000-000000000001', 'Google', 'Bad billing day', 1, 'GBP', 'Monthly', 32, '2026-01-01')
                    """,
                """
                    DELETE FROM "Subscriptions" WHERE "Id" = '60000000-0000-0000-0000-000000000001'
                    """,
                "CK_Subscription_BillingDay_Valid",
            },
            new object[]
            {
                """
                    INSERT INTO "SpendEntries" ("Id", "OccurredOn", "VendorId", "CategoryId", "Amount", "Currency", "AmountGbp", "FxRate", "Source", "RecordedAt")
                    SELECT '60000000-0000-0000-0000-000000000002', '2026-08-01',
                           (SELECT "Id" FROM "SpendVendors" WHERE "Key" = 'github-actions'),
                           (SELECT "Id" FROM "SpendCategories" WHERE "Key" = 'ci'),
                           1, 'usd', 1, 1, 'Portal', '2026-08-31T00:00:00Z'
                    """,
                """
                    DELETE FROM "SpendEntries" WHERE "Id" = '60000000-0000-0000-0000-000000000002'
                    """,
                "CK_SpendEntry_Currency_Normalized",
            },
            new object[]
            {
                """
                    INSERT INTO "NotificationSettings" ("Id", "UpdatedAt")
                    VALUES ('33333333-3333-3333-3333-333333333399', '2026-08-31T00:00:00Z')
                    """,
                """
                    DELETE FROM "NotificationSettings" WHERE "Id" = '33333333-3333-3333-3333-333333333399'
                    """,
                "CK_NotificationSettings_Singleton",
            },
            new object[]
            {
                """
                    INSERT INTO "DailyAggregates" ("Date", "Provider", "Model", "InputTokens", "OutputTokens", "CacheReadTokens", "CacheWriteTokens", "CacheWrite1hTokens", "CostUsd", "RequestCount", "UnknownCacheSavingsCount")
                    VALUES ('2026-08-31', 'OpenAI', 'gpt-5.4', 1, 1, 0, 0, 0, 1, 1, -1)
                    """,
                """
                    DELETE FROM "DailyAggregates" WHERE "Date" = '2026-08-31' AND "Provider" = 'OpenAI' AND "Model" = 'gpt-5.4'
                    """,
                "CK_DailyAggregate_UnknownCacheSavingsCount_NonNegative",
            },
        };

    // Rows written before the C# guards existed are unaudited: a plain ADD CONSTRAINT would
    // abort the whole migration with a bare check-violation and no row list, so the
    // migration refuses up front with an actionable message instead.
    [Theory]
    [MemberData(nameof(SchemaGuardViolationSeeds))]
    public async Task EnforceSchemaGuardConstraints_RefusesWhileHistoricRowsViolate(
        string seedSql,
        string cleanupSql,
        string constraintName
    )
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = new AiObservatoryDbContext(_options);
        var migrator = db.Database.GetService<IMigrator>();
        await migrator.MigrateAsync("20260831001140_AddUsageEventCorrectedAt", ct);
        await db.Database.ExecuteSqlRawAsync(seedSql, ct);

        var refused = () => migrator.MigrateAsync("20260831010733_EnforceSchemaGuardConstraints", ct);

        await refused.Should().ThrowAsync<PostgresException>().WithMessage($"*refused*{constraintName}*");

        // Once the violating row is reconciled, the same migration applies and the
        // constraint holds.
        await db.Database.ExecuteSqlRawAsync(cleanupSql, ct);
        await migrator.MigrateAsync(cancellationToken: ct);
        var enforced = await db
            .Database.SqlQuery<int>(
                $"""SELECT count(*)::int AS "Value" FROM pg_constraint WHERE conname = {constraintName}"""
            )
            .SingleAsync(ct);
        enforced.Should().Be(1);
    }

    public static IEnumerable<object[]> TightenedGuardViolationSeeds =>
        new[]
        {
            new object[]
            {
                """
                    INSERT INTO "UsageEvents" ("Id", "Provider", "OccurredAt", "IngestedAt", "InputTokens", "OutputTokens", "CostUsd", "RawPayload", "SourceId", "SourceKind", "UsageScope", "CostBasis", "ObservedAt")
                    VALUES ('60000000-0000-0000-0000-000000000003', 'OpenAI', '2026-08-31T00:30:00Z', '2026-08-31T00:30:00Z', 1, 1, 5, jsonb_build_object(), 'openai-usage-api', 'ProviderApi', 'Api', 'None', '2026-08-31T00:30:00Z')
                    """,
                """
                    DELETE FROM "UsageEvents" WHERE "Id" = '60000000-0000-0000-0000-000000000003'
                    """,
                "CK_UsageEvent_NoneCostBasis_NoCost",
            },
            new object[]
            {
                """
                    INSERT INTO "BillingObservations" ("Id", "ProviderKey", "SourceId", "SourceKind", "UsageScope", "CostBasis", "ObservationKey", "OccurredOn", "Currency", "GrossAmount", "CreditAmount", "NetAmount", "RawPayload", "ObservedAt")
                    VALUES ('60000000-0000-0000-0000-000000000004', 'openai', 'openai-costs-api', 'ProviderApi', 'Api', 'Billed', '2026-08:positive-credit', '2026-08-01', 'USD', 8, 2, 10, jsonb_build_object(), '2026-08-31T00:00:00Z')
                    """,
                """
                    DELETE FROM "BillingObservations" WHERE "Id" = '60000000-0000-0000-0000-000000000004'
                    """,
                "CK_BillingObservation_Credit_Sign",
            },
        };

    [Theory]
    [MemberData(nameof(TightenedGuardViolationSeeds))]
    public async Task ConvertBudgetThresholdsToGbpAndTightenGuards_RefusesWhileHistoricRowsViolate(
        string seedSql,
        string cleanupSql,
        string constraintName
    )
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = new AiObservatoryDbContext(_options);
        var migrator = db.Database.GetService<IMigrator>();
        await migrator.MigrateAsync("20260831012811_ProtectSlackWebhookUrlAtRest", ct);
        await db.Database.ExecuteSqlRawAsync(seedSql, ct);

        var refused = () => migrator.MigrateAsync("20260831085032_ConvertBudgetThresholdsToGbpAndTightenGuards", ct);

        await refused.Should().ThrowAsync<PostgresException>().WithMessage($"*refused*{constraintName}*");

        await db.Database.ExecuteSqlRawAsync(cleanupSql, ct);
        await migrator.MigrateAsync(cancellationToken: ct);
        var enforced = await db
            .Database.SqlQuery<int>(
                $"""SELECT count(*)::int AS "Value" FROM pg_constraint WHERE conname = {constraintName}"""
            )
            .SingleAsync(ct);
        enforced.Should().Be(1);
    }

    [Fact]
    public async Task DropObservationProvenanceDefaults_RemovesProvenanceDefaultsAfterBackfill()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = new AiObservatoryDbContext(_options);
        var migrator = db.Database.GetService<IMigrator>();

        // AddObservationProvenance is already recorded in production with the defaults
        // still present, so its restored Up leaves them in place; dropping them is the
        // job of the later DropObservationProvenanceDefaults migration on every database.
        await migrator.MigrateAsync("20260824172007_AddObservationProvenance", ct);
        var defaultsAtProvenance = await ProvenanceDefaultsAsync(db, ct);
        defaultsAtProvenance.Should().HaveCount(14);

        await migrator.MigrateAsync(cancellationToken: ct);
        var lingeringDefaults = await ProvenanceDefaultsAsync(db, ct);
        lingeringDefaults.Should().BeEmpty();

        // With the defaults gone, a direct insert that omits provenance fails rather than
        // silently acquiring synthetic 'legacy-api' / 'Legacy' / epoch values.
        var omitProvenance = () =>
            db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "UsageEvents" ("Id", "Provider", "OccurredAt", "IngestedAt", "InputTokens", "OutputTokens", "RawPayload")
                VALUES ('70000000-0000-0000-0000-000000000001', 'OpenAI', '2026-09-01T00:30:00Z', '2026-09-01T00:30:00Z', 1, 1, jsonb_build_object())
                """,
                ct
            );
        await omitProvenance.Should().ThrowAsync<PostgresException>();

        // The drop survives a rollback and re-apply: Down re-adds the defaults, and the
        // replayed Up removes them again without error (DROP DEFAULT is a no-op when no
        // default exists).
        await migrator.MigrateAsync("20260824172007_AddObservationProvenance", ct);
        var restoredDefaults = await ProvenanceDefaultsAsync(db, ct);
        restoredDefaults.Should().HaveCount(14);
        await migrator.MigrateAsync(cancellationToken: ct);
        var defaultsAfterReplay = await ProvenanceDefaultsAsync(db, ct);
        defaultsAfterReplay.Should().BeEmpty();
    }

    private static async Task<List<string>> ProvenanceDefaultsAsync(AiObservatoryDbContext db, CancellationToken ct) =>
        await db
            .Database.SqlQueryRaw<string>(
                """
                SELECT table_name || '.' || column_name AS "Value"
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND column_default IS NOT NULL
                  AND (
                      (table_name = 'UsageEvents'
                       AND column_name IN ('SourceId', 'SourceKind', 'UsageScope', 'CostBasis', 'ObservedAt'))
                      OR (table_name = 'SpendEntries'
                       AND column_name IN ('SourceId', 'SourceKind', 'UsageScope', 'CostBasis', 'ObservedAt'))
                      OR (table_name = 'DailyAggregates'
                       AND column_name IN ('SourceId', 'SourceKind', 'UsageScope', 'CostBasis'))
                  )
                ORDER BY 1
                """
            )
            .ToListAsync(ct);

    [Fact]
    public async Task AddUsageEventTelemetryIdentity_RollbackRefusesWhileUnknownCostsExist()
    {
        var ct = TestContext.Current.CancellationToken;
        const string rawPayload = "{}";
        await using (var db = new AiObservatoryDbContext(_options))
        {
            var migrator = db.Database.GetService<IMigrator>();
            await migrator.MigrateAsync("20260812022935_AddUsageEventTelemetryIdentity", ct);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "UsageEvents" ("Id", "Provider", "OccurredAt", "IngestedAt", "Model", "InputTokens", "OutputTokens", "CostUsd", "RawPayload", "EventKey")
                VALUES ('50000000-0000-0000-0000-000000000001', 'OpenAI', '2026-08-12T00:30:00Z', '2026-08-12T00:30:00Z', NULL, 1, 1, NULL, CAST({rawPayload} AS jsonb), 'rollback-guard-null-cost')
                """,
                ct
            );

            var rollback = () => migrator.MigrateAsync("20260804192732_AddIdeEvents", ct);

            await rollback.Should().ThrowAsync<PostgresException>().WithMessage("*rollback refused*");
        }

        // Once the unknown-cost row is resolved, the same rollback succeeds untouched.
        await using (var db = new AiObservatoryDbContext(_options))
        {
            await db.Database.ExecuteSqlRawAsync(
                """DELETE FROM "UsageEvents" WHERE "EventKey" = 'rollback-guard-null-cost'""",
                ct
            );
            var migrator = db.Database.GetService<IMigrator>();
            await migrator.MigrateAsync("20260804192732_AddIdeEvents", ct);
            var telemetryColumns = await db
                .Database.SqlQueryRaw<int>(
                    """
                    SELECT count(*)::int AS "Value"
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'UsageEvents'
                      AND column_name IN ('AgentId', 'Runtime', 'SessionId', 'ThoughtTokens')
                    """
                )
                .SingleAsync(ct);
            telemetryColumns.Should().Be(0);
        }
    }

    [Fact]
    public async Task BillingObservation_RejectsPositiveCreditAmount()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = new AiObservatoryDbContext(_options);
        await db.Database.MigrateAsync(ct);
        // Bypass the writer (which now throws up front) to prove the schema guard holds for
        // direct writes: a positive credit balances Gross + Credit = Net while inflating
        // net billed spend.
        db.BillingObservations.Add(
            new BillingObservation
            {
                ProviderKey = "openai",
                SourceId = UsageSourceIds.OpenAiCostsApi,
                SourceKind = SourceKind.ProviderApi,
                UsageScope = UsageScope.Api,
                CostBasis = CostBasis.Billed,
                ObservationKey = "2026-08:positive-credit",
                OccurredOn = new LocalDate(2026, 8, 1),
                Currency = "USD",
                GrossAmount = 8m,
                CreditAmount = 2m,
                NetAmount = 10m,
                ObservedAt = Instant.FromUtc(2026, 8, 31, 0, 0),
            }
        );

        var save = () => db.SaveChangesAsync(ct);

        await save.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task UsageEvent_RejectsPositiveCostUnderNoneCostBasis()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = new AiObservatoryDbContext(_options);
        await db.Database.MigrateAsync(ct);
        var observedAt = Instant.FromUtc(2026, 8, 31, 0, 0);
        db.UsageEvents.Add(
            new UsageEvent
            {
                Provider = Provider.OpenAI,
                OccurredAt = observedAt,
                IngestedAt = observedAt,
                InputTokens = 1,
                OutputTokens = 1,
                CostUsd = 1m,
                SourceId = UsageSourceIds.OpenAiUsageApi,
                SourceKind = SourceKind.ProviderApi,
                UsageScope = UsageScope.Api,
                CostBasis = CostBasis.None,
                ObservedAt = observedAt,
            }
        );

        var save = () => db.SaveChangesAsync(ct);

        await save.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task AddObservationProvenance_RollbackRefusesWhileLegacyKeyGroupsAreSplitAcrossLanes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = new AiObservatoryDbContext(_options);
        var migrator = db.Database.GetService<IMigrator>();
        await migrator.MigrateAsync("20260824172007_AddObservationProvenance", ct);
        // Two provenance lanes sharing (Date, Provider, Model): restoring the legacy
        // three-column key would collide on these rows.
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "DailyAggregates" ("Date", "Provider", "Model", "InputTokens", "OutputTokens", "CacheReadTokens", "CacheWriteTokens", "CacheWrite1hTokens", "CostUsd", "RequestCount", "SourceId", "SourceKind", "UsageScope", "CostBasis")
            VALUES
                ('2026-08-24', 'OpenAI', 'gpt-5.4', 1, 1, 0, 0, 0, 1, 1, 'legacy-api', 'Legacy', 'Unknown', 'Unknown'),
                ('2026-08-24', 'OpenAI', 'gpt-5.4', 1, 1, 0, 0, 0, 1, 1, 'openai-usage-api', 'ProviderApi', 'Api', 'Billed')
            """,
            ct
        );

        var rollback = () => migrator.MigrateAsync("20260812024132_AddUnknownCostCoverage", ct);

        await rollback.Should().ThrowAsync<PostgresException>().WithMessage("*rollback refused*");

        // Once an operator consolidates the split lanes, the same rollback succeeds.
        await db.Database.ExecuteSqlRawAsync(
            """DELETE FROM "DailyAggregates" WHERE "SourceId" = 'openai-usage-api'""",
            ct
        );
        await migrator.MigrateAsync("20260812024132_AddUnknownCostCoverage", ct);
        var provenanceColumns = await db
            .Database.SqlQueryRaw<int>(
                """
                SELECT count(*)::int AS "Value"
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name = 'DailyAggregates'
                  AND column_name IN ('SourceId', 'SourceKind', 'UsageScope', 'CostBasis')
                """
            )
            .SingleAsync(ct);
        provenanceColumns.Should().Be(0);
    }

    [Fact]
    public async Task AddBillingObservations_RollbackKeepsReferencedCategory()
    {
        var ct = TestContext.Current.CancellationToken;
        var apiUsageCategoryId = Guid.Parse("11111111-1111-1111-1111-111111111106");
        await using var db = new AiObservatoryDbContext(_options);
        var migrator = db.Database.GetService<IMigrator>();
        await migrator.MigrateAsync("20260825033709_AddBillingObservations", ct);
        var vendorId = await db
            .SpendVendors.Where(vendor => vendor.Key == "github-actions")
            .Select(vendor => vendor.Id)
            .SingleAsync(ct);
        db.SpendEntries.Add(
            new SpendEntry
            {
                OccurredOn = new LocalDate(2026, 8, 25),
                VendorId = vendorId,
                CategoryId = apiUsageCategoryId,
                Amount = 1m,
                Currency = "GBP",
                AmountGbp = 1m,
                FxRate = 1m,
                Source = SpendSource.Api,
                EntryKey = "rollback-guard-referenced-category",
                RecordedAt = Instant.FromUtc(2026, 8, 25, 0, 0),
                SourceId = UsageSourceIds.GitHubBillingApi,
                SourceKind = SourceKind.ProviderApi,
                UsageScope = UsageScope.Api,
                CostBasis = CostBasis.Billed,
                ObservedAt = Instant.FromUtc(2026, 8, 25, 0, 0),
            }
        );
        await db.SaveChangesAsync(ct);

        // The Restrict FK blocks deleting a referenced category; the rollback keeps it
        // instead of aborting with a foreign-key violation.
        await migrator.MigrateAsync("20260824215707_AddPricingSnapshots", ct);
        var surviving = await db
            .Database.SqlQuery<Guid>(
                $"""SELECT "Id" AS "Value" FROM "SpendCategories" WHERE "Id" = {apiUsageCategoryId}"""
            )
            .ToListAsync(ct);
        surviving.Should().ContainSingle();
    }

    [Fact]
    public async Task AddBillingObservations_RollbackDeletesUnreferencedCategory()
    {
        var ct = TestContext.Current.CancellationToken;
        var apiUsageCategoryId = Guid.Parse("11111111-1111-1111-1111-111111111106");
        await using var db = new AiObservatoryDbContext(_options);
        var migrator = db.Database.GetService<IMigrator>();
        await migrator.MigrateAsync("20260825033709_AddBillingObservations", ct);
        await migrator.MigrateAsync("20260824215707_AddPricingSnapshots", ct);

        var surviving = await db
            .Database.SqlQuery<Guid>(
                $"""SELECT "Id" AS "Value" FROM "SpendCategories" WHERE "Id" = {apiUsageCategoryId}"""
            )
            .ToListAsync(ct);

        surviving.Should().BeEmpty();
    }
}
