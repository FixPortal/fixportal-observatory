using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBudgetAlertsAndRenameThresholdToGbp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(name: "ThresholdUsd", table: "BudgetRules", newName: "ThresholdGbp");

            migrationBuilder.AddColumn<LocalDate>(
                name: "EvaluationStartsOn",
                table: "BudgetRules",
                type: "date",
                nullable: false,
                defaultValueSql: "(CURRENT_TIMESTAMP AT TIME ZONE 'UTC')::date"
            );

            migrationBuilder.CreateTable(
                name: "BudgetAlertClaims",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BudgetRuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    PeriodStart = table.Column<LocalDate>(type: "date", nullable: false),
                    PeriodEnd = table.Column<LocalDate>(type: "date", nullable: false),
                    InsightId = table.Column<Guid>(type: "uuid", nullable: false),
                    ThresholdGbp = table.Column<decimal>(type: "numeric", nullable: false),
                    ActualSpendGbp = table.Column<decimal>(type: "numeric", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    EmailLeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    EmailLeaseAcquiredAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    EmailSentAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BudgetAlertClaims", x => x.Id);
                    table.CheckConstraint(
                        "CK_BudgetAlertClaim_EmailLease",
                        "(\"EmailLeaseId\" IS NULL) = (\"EmailLeaseAcquiredAt\" IS NULL)"
                    );
                    table.CheckConstraint("CK_BudgetAlertClaim_Period", "\"PeriodEnd\" >= \"PeriodStart\"");
                    table.ForeignKey(
                        name: "FK_BudgetAlertClaims_BudgetRules_BudgetRuleId",
                        column: x => x.BudgetRuleId,
                        principalTable: "BudgetRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                    table.ForeignKey(
                        name: "FK_BudgetAlertClaims_Insights_InsightId",
                        column: x => x.InsightId,
                        principalTable: "Insights",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder
                .CreateIndex(
                    name: "IX_BudgetAlertClaims_Deliverable",
                    table: "BudgetAlertClaims",
                    columns: new[] { "CreatedAt", "Id" },
                    filter: "\"EmailSentAt\" IS NULL"
                )
                .Annotation("Npgsql:IndexInclude", new[] { "EmailLeaseAcquiredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BudgetAlertClaims_InsightId",
                table: "BudgetAlertClaims",
                column: "InsightId",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "UX_BudgetAlertClaims_RulePeriod",
                table: "BudgetAlertClaims",
                columns: new[] { "BudgetRuleId", "PeriodStart", "PeriodEnd" },
                unique: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "BudgetAlertClaims");

            migrationBuilder.DropColumn(name: "EvaluationStartsOn", table: "BudgetRules");

            migrationBuilder.RenameColumn(name: "ThresholdGbp", table: "BudgetRules", newName: "ThresholdUsd");
        }
    }
}
