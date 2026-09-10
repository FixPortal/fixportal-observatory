using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReconcileDailyAggregatesFromEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Reconcile DailyAggregates from UsageEvents once, so the CK_DailyAggregate_*_NonNegative
            // constraints (added without a rebuild) can be trusted: a row whose totals drifted below
            // the sum of its events under the pre-constraint code would pass the constraint at add
            // time and only go negative on the first later correction/reprice touching it. The sums
            // mirror UsageRepository.ApplyAggregateDeltaAsync semantics exactly (null cost/savings
            // count as unknown only when the basis is not None, which declares "no price applies").
            // Rows whose events are gone entirely are left alone: they cannot be distinguished from
            // legitimately maintained rows without a read the delta discipline never took.
            migrationBuilder.Sql(
                """
                INSERT INTO "DailyAggregates" ("Date", "Provider", "Model", "SourceId", "SourceKind", "UsageScope", "CostBasis", "InputTokens", "OutputTokens", "CacheReadTokens", "CacheWriteTokens", "CacheWrite1hTokens", "CostUsd", "UnknownCostCount", "CacheSavingsUsd", "UnknownCacheSavingsCount", "RequestCount")
                SELECT
                    ("OccurredAt" AT TIME ZONE 'UTC')::date,
                    "Provider",
                    COALESCE("Model", 'unknown'),
                    "SourceId",
                    "SourceKind",
                    "UsageScope",
                    "CostBasis",
                    SUM("InputTokens"),
                    SUM("OutputTokens"),
                    SUM(COALESCE("CacheReadTokens", 0)),
                    SUM(COALESCE("CacheWriteTokens", 0)),
                    SUM(COALESCE("CacheWrite1hTokens", 0)),
                    SUM(COALESCE("CostUsd", 0)),
                    SUM(CASE WHEN "CostUsd" IS NULL AND "CostBasis" <> 'None' THEN 1 ELSE 0 END),
                    SUM(COALESCE("CacheSavingsUsd", 0)),
                    SUM(CASE WHEN "CacheSavingsUsd" IS NULL AND "CostBasis" <> 'None' THEN 1 ELSE 0 END),
                    COUNT(*)
                FROM "UsageEvents"
                GROUP BY 1, 2, 3, 4, 5, 6, 7
                ON CONFLICT ("Date", "Provider", "Model", "SourceId", "SourceKind", "UsageScope", "CostBasis") DO UPDATE SET
                    "InputTokens" = EXCLUDED."InputTokens",
                    "OutputTokens" = EXCLUDED."OutputTokens",
                    "CacheReadTokens" = EXCLUDED."CacheReadTokens",
                    "CacheWriteTokens" = EXCLUDED."CacheWriteTokens",
                    "CacheWrite1hTokens" = EXCLUDED."CacheWrite1hTokens",
                    "CostUsd" = EXCLUDED."CostUsd",
                    "UnknownCostCount" = EXCLUDED."UnknownCostCount",
                    "CacheSavingsUsd" = EXCLUDED."CacheSavingsUsd",
                    "UnknownCacheSavingsCount" = EXCLUDED."UnknownCacheSavingsCount",
                    "RequestCount" = EXCLUDED."RequestCount"
                WHERE
                    "DailyAggregates"."InputTokens" IS DISTINCT FROM EXCLUDED."InputTokens"
                    OR "DailyAggregates"."OutputTokens" IS DISTINCT FROM EXCLUDED."OutputTokens"
                    OR "DailyAggregates"."CacheReadTokens" IS DISTINCT FROM EXCLUDED."CacheReadTokens"
                    OR "DailyAggregates"."CacheWriteTokens" IS DISTINCT FROM EXCLUDED."CacheWriteTokens"
                    OR "DailyAggregates"."CacheWrite1hTokens" IS DISTINCT FROM EXCLUDED."CacheWrite1hTokens"
                    OR "DailyAggregates"."CostUsd" IS DISTINCT FROM EXCLUDED."CostUsd"
                    OR "DailyAggregates"."UnknownCostCount" IS DISTINCT FROM EXCLUDED."UnknownCostCount"
                    OR "DailyAggregates"."CacheSavingsUsd" IS DISTINCT FROM EXCLUDED."CacheSavingsUsd"
                    OR "DailyAggregates"."UnknownCacheSavingsCount" IS DISTINCT FROM EXCLUDED."UnknownCacheSavingsCount"
                    OR "DailyAggregates"."RequestCount" IS DISTINCT FROM EXCLUDED."RequestCount"
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A reconcile cannot be undone: the drifted values it corrected are not recoverable.
        }
    }
}
