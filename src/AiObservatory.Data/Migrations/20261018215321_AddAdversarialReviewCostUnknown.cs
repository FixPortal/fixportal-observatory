using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAdversarialReviewCostUnknown : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CostUnknown",
                table: "AdversarialReviewRuns",
                type: "boolean",
                nullable: false,
                defaultValue: false
            );

            // A recorded cost of exactly zero was never a measurement for these runs (subscription or no telemetry): unknown, not free.
            migrationBuilder.Sql(
                "UPDATE \"AdversarialReviewRuns\" SET \"CostUnknown\" = true, \"CostPerAcceptedFinding\" = NULL WHERE \"CostUsd\" = 0;"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "CostUnknown", table: "AdversarialReviewRuns");
        }
    }
}
