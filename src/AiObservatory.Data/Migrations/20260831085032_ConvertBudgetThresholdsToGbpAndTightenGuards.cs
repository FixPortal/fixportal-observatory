using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class ConvertBudgetThresholdsToGbpAndTightenGuards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // D2/NS-2: AddBudgetAlertsAndRenameThresholdToGbp renamed ThresholdUsd to
            // ThresholdGbp with no conversion, so a pre-existing rule entered in USD kept
            // its number as a GBP figure (~20% looser) from the first evaluation after
            // that deploy. Convert the stored values once, pinned to the ledger's
            // documented USD->GBP fallback rate (FxRateProvider, 0.79). Rows entered in
            // GBP between the rename and this migration cannot be distinguished and would
            // over-convert; on a fresh database this updates no rows at all.
            // The marker check keeps "once" honest: Down deliberately does not un-convert
            // (re-dividing would compound rounding), so a rollback followed by a re-apply
            // would convert a second time and shave another ~21% off every threshold.
            // GuardBudgetThresholdGbpConversion records the marker row; where it exists,
            // this replay skips. Where the markers table does not exist yet (a database
            // migrating forward through this migration for the first time), the conversion
            // runs and the marker is recorded when that later migration applies.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF to_regclass('"DataMigrationMarkers"') IS NULL THEN
                        -- The markers table is created by the later GuardBudgetThresholdGbpConversion
                        -- migration, so its absence means a first-time apply: convert. The
                        -- table is referenced only in the ELSIF branch because PL/pgSQL
                        -- parse-analyses each expression on first evaluation, and evaluating
                        -- a query against a missing relation would abort this migration.
                        UPDATE "BudgetRules" SET "ThresholdGbp" = round("ThresholdGbp" * 0.79, 2);
                    ELSIF EXISTS (
                        SELECT 1 FROM "DataMigrationMarkers" WHERE "Name" = 'BudgetThresholdsConvertedToGbp'
                    ) THEN
                        RAISE NOTICE 'BudgetRules thresholds are already recorded as converted to GBP; skipping the conversion replay.';
                    ELSE
                        UPDATE "BudgetRules" SET "ThresholdGbp" = round("ThresholdGbp" * 0.79, 2);
                    END IF;
                END $$;
                """
            );

            // D7: the (CURRENT_TIMESTAMP ... )::date default doubled as the backfill for
            // pre-existing rules and then lingered on the column, silently stamping "today"
            // onto any later insert that omitted it. Existing rows keep their original
            // deploy-day start (re-backfilling now cannot tell those apart from values set
            // deliberately since); the default goes away so omissions fail instead.
            migrationBuilder.AlterColumn<LocalDate>(
                name: "EvaluationStartsOn",
                table: "BudgetRules",
                type: "date",
                nullable: false,
                oldClrType: typeof(LocalDate),
                oldType: "date",
                oldDefaultValueSql: "(CURRENT_TIMESTAMP AT TIME ZONE 'UTC')::date"
            );

            // As in EnforceSchemaGuardConstraints, refuse with an actionable message when
            // historic rows already violate a constraint being added, instead of aborting
            // the whole migration with a bare check-violation and no row list.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "UsageEvents" WHERE NOT ("CostBasis" <> 'None' OR "CostUsd" IS NULL OR "CostUsd" = 0)) THEN
                        RAISE EXCEPTION 'ConvertBudgetThresholdsToGbpAndTightenGuards refused: % UsageEvents rows violate CK_UsageEvent_NoneCostBasis_NoCost ("CostBasis" <> ''None'' OR "CostUsd" IS NULL OR "CostUsd" = 0). First violating ids: %. Reconcile or remove them, then re-run the migration.',
                            (SELECT count(*) FROM "UsageEvents" WHERE NOT ("CostBasis" <> 'None' OR "CostUsd" IS NULL OR "CostUsd" = 0)),
                            (SELECT string_agg("Id"::text, ', ') FROM (SELECT "Id" FROM "UsageEvents" WHERE NOT ("CostBasis" <> 'None' OR "CostUsd" IS NULL OR "CostUsd" = 0) LIMIT 10) violating);
                    END IF;
                END $$;
                """
            );

            migrationBuilder.AddCheckConstraint(
                name: "CK_UsageEvent_NoneCostBasis_NoCost",
                table: "UsageEvents",
                sql: "\"CostBasis\" <> 'None' OR \"CostUsd\" IS NULL OR \"CostUsd\" = 0"
            );

            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "BillingObservations" WHERE NOT ("CreditAmount" <= 0)) THEN
                        RAISE EXCEPTION 'ConvertBudgetThresholdsToGbpAndTightenGuards refused: % BillingObservations rows violate CK_BillingObservation_Credit_Sign ("CreditAmount" <= 0). First violating ids: %. Reconcile or remove them, then re-run the migration.',
                            (SELECT count(*) FROM "BillingObservations" WHERE NOT ("CreditAmount" <= 0)),
                            (SELECT string_agg("Id"::text, ', ') FROM (SELECT "Id" FROM "BillingObservations" WHERE NOT ("CreditAmount" <= 0) LIMIT 10) violating);
                    END IF;
                END $$;
                """
            );

            migrationBuilder.AddCheckConstraint(
                name: "CK_BillingObservation_Credit_Sign",
                table: "BillingObservations",
                sql: "\"CreditAmount\" <= 0"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(name: "CK_UsageEvent_NoneCostBasis_NoCost", table: "UsageEvents");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BillingObservation_Credit_Sign",
                table: "BillingObservations"
            );

            migrationBuilder.AlterColumn<LocalDate>(
                name: "EvaluationStartsOn",
                table: "BudgetRules",
                type: "date",
                nullable: false,
                defaultValueSql: "(CURRENT_TIMESTAMP AT TIME ZONE 'UTC')::date",
                oldClrType: typeof(LocalDate),
                oldType: "date"
            );
        }
    }
}
