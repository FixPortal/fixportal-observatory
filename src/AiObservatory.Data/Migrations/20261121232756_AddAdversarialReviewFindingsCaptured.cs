using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAdversarialReviewFindingsCaptured : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "FindingsCaptured",
                table: "AdversarialReviewRuns",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Backfill: a run that already has finding rows certainly had detail captured. A clean
            // run posted as [] before this column existed left no trace, so it stays counts-only
            // until its findings are posted again.
            migrationBuilder.Sql(
                "UPDATE \"AdversarialReviewRuns\" r SET \"FindingsCaptured\" = TRUE WHERE EXISTS (SELECT 1 FROM \"AdversarialReviewFindings\" f WHERE f.\"RunId\" = r.\"RunId\");");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FindingsCaptured",
                table: "AdversarialReviewRuns");
        }
    }
}
