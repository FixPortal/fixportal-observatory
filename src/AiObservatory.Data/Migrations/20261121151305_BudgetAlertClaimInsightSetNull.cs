using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class BudgetAlertClaimInsightSetNull : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BudgetAlertClaims_Insights_InsightId",
                table: "BudgetAlertClaims");

            migrationBuilder.AlterColumn<Guid>(
                name: "InsightId",
                table: "BudgetAlertClaims",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddForeignKey(
                name: "FK_BudgetAlertClaims_Insights_InsightId",
                table: "BudgetAlertClaims",
                column: "InsightId",
                principalTable: "Insights",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Tombstoned claims have no insight to point at and the old non-null column cannot hold them,
            // but deleting them would silently re-arm every budget alert that already fired. Refuse instead.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "BudgetAlertClaims" WHERE "InsightId" IS NULL) THEN
                        RAISE EXCEPTION 'BudgetAlertClaimInsightSetNull rollback refused: % BudgetAlertClaims rows are tombstoned ("InsightId" IS NULL); deleting them would re-arm budget alerts that already fired. Reconcile them before rolling back.',
                            (SELECT count(*) FROM "BudgetAlertClaims" WHERE "InsightId" IS NULL);
                    END IF;
                END $$;
                """
            );

            migrationBuilder.DropForeignKey(
                name: "FK_BudgetAlertClaims_Insights_InsightId",
                table: "BudgetAlertClaims");

            migrationBuilder.AlterColumn<Guid>(
                name: "InsightId",
                table: "BudgetAlertClaims",
                type: "uuid",
                nullable: false,
                defaultValue: Guid.Empty,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_BudgetAlertClaims_Insights_InsightId",
                table: "BudgetAlertClaims",
                column: "InsightId",
                principalTable: "Insights",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
