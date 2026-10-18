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

            // The z.AI wrapper has never produced a real cost; its stored zeros are unknown, not free.
            migrationBuilder.Sql(
                "UPDATE \"AdversarialReviewRuns\" SET \"CostUnknown\" = true, \"CostPerAcceptedFinding\" = NULL WHERE \"Reviewer\" = 'zai';"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "CostUnknown", table: "AdversarialReviewRuns");
        }
    }
}
