using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUnknownCostCoverage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "UnknownCostCount",
                table: "DailyAggregates",
                type: "integer",
                nullable: false,
                defaultValue: 0
            );

            migrationBuilder.Sql(
                """
                UPDATE "DailyAggregates" AS aggregate
                SET "UnknownCostCount" = source."UnknownCostCount"
                FROM (
                    SELECT
                        ("OccurredAt" AT TIME ZONE 'UTC')::date AS "Date",
                        "Provider",
                        COALESCE("Model", 'unknown') AS "Model",
                        COUNT(*)::integer AS "UnknownCostCount"
                    FROM "UsageEvents"
                    WHERE "CostUsd" IS NULL
                    GROUP BY ("OccurredAt" AT TIME ZONE 'UTC')::date, "Provider", COALESCE("Model", 'unknown')
                ) AS source
                WHERE aggregate."Date" = source."Date"
                  AND aggregate."Provider" = source."Provider"
                  AND aggregate."Model" = source."Model"
                """
            );

            migrationBuilder.AddCheckConstraint(
                name: "CK_DailyAggregate_UnknownCostCount_NonNegative",
                table: "DailyAggregates",
                sql: "\"UnknownCostCount\" >= 0"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_DailyAggregate_UnknownCostCount_NonNegative",
                table: "DailyAggregates"
            );

            migrationBuilder.DropColumn(name: "UnknownCostCount", table: "DailyAggregates");
        }
    }
}
