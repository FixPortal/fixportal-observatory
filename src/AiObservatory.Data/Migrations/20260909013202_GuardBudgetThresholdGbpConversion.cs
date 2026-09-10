using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class GuardBudgetThresholdGbpConversion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ConvertBudgetThresholdsToGbpAndTightenGuards converted BudgetRules thresholds
            // USD->GBP exactly once, but its Down deliberately does not un-convert, so a
            // rollback + re-apply would convert again (~21% off every threshold per cycle).
            // Record that the conversion ran in a table outside EF's migration history (a
            // rollback deletes history rows, which is exactly what must not disarm the
            // guard); the conversion migration's Up checks this marker and skips replays.
            migrationBuilder.Sql(
                """
                CREATE TABLE IF NOT EXISTS "DataMigrationMarkers" (
                    "Name" text NOT NULL,
                    "AppliedAt" timestamp with time zone NOT NULL DEFAULT now(),
                    CONSTRAINT "PK_DataMigrationMarkers" PRIMARY KEY ("Name")
                );

                INSERT INTO "DataMigrationMarkers" ("Name")
                VALUES ('BudgetThresholdsConvertedToGbp')
                ON CONFLICT DO NOTHING;
                """
            );

            // Rules created through /budget-rules between the 2026-08-26 rename deploy and
            // the 2026-08-31 conversion deploy were entered in GBP and then converted
            // anyway, leaving them ~21% too low. BudgetRule carries no created timestamp,
            // so those rows cannot be identified by a predicate: surface every rule in the
            // deploy log for operator review instead of converting anything automatically,
            // AND persist one durable review marker per rule — Npgsql logs NOTICEs at
            // Debug while production logging starts at Information and the deploy workflow
            // retains no migration output, so the NOTICE alone never reaches an operator.
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    rule_count integer;
                    rule_list text;
                BEGIN
                    SELECT count(*) INTO rule_count FROM "BudgetRules";
                    IF rule_count > 0 THEN
                        SELECT string_agg(
                            "Id"::text || ' Provider=' || coalesce("Provider", '(all)') || ' Period=' || "Period" || ' ThresholdGbp=' || "ThresholdGbp",
                            '; '
                        )
                        INTO rule_list
                        FROM (SELECT "Id", "Provider", "Period", "ThresholdGbp" FROM "BudgetRules" ORDER BY "Id" LIMIT 50) rules;
                        RAISE NOTICE 'BudgetRules GBP conversion needs operator review: % rule(s) exist. Thresholds were converted USD->GBP at 0.79 on 2026-08-31; any rule created between the 2026-08-26 rename and that conversion was already GBP and is now ~21%% too low. Review via GET /budget-rules and correct any affected rule: %',
                            rule_count, rule_list;

                        -- Durable copy of the review list, one row per rule, queryable via
                        -- SELECT * FROM "DataMigrationMarkers" WHERE "Name" LIKE
                        -- 'budget-threshold-gbp-conversion:review:%'. No 50-row cap here:
                        -- the table is cheap and the review must cover every rule. The
                        -- ON CONFLICT keeps a rollback + re-apply idempotent (Down
                        -- deliberately keeps the markers, exactly like the conversion
                        -- marker above).
                        INSERT INTO "DataMigrationMarkers" ("Name")
                        SELECT 'budget-threshold-gbp-conversion:review:' || "Id"::text
                        FROM "BudgetRules"
                        ON CONFLICT DO NOTHING;
                    END IF;
                END $$;
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The marker records a fact about the data, not the schema version: once the
            // GBP conversion has run, replays must stay skipped even across rollbacks of
            // this migration and the conversion migration. Dropping the marker here would
            // re-arm the double conversion it exists to prevent, so Down leaves it.
        }
    }
}
