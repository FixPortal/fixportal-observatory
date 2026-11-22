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
            // A clean run (captured, no finding rows) cannot be re-derived by Up's backfill, so dropping
            // the column would silently turn it back into a counts-only run. Refuse instead.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "AdversarialReviewRuns" r WHERE r."FindingsCaptured"
                               AND NOT EXISTS (SELECT 1 FROM "AdversarialReviewFindings" f WHERE f."RunId" = r."RunId")) THEN
                        RAISE EXCEPTION 'AddAdversarialReviewFindingsCaptured rollback refused: % run(s) are marked captured with no finding rows (clean runs); dropping the column would turn them back into counts-only runs. Reconcile them before rolling back.',
                            (SELECT count(DISTINCT r."RunId") FROM "AdversarialReviewRuns" r WHERE r."FindingsCaptured"
                             AND NOT EXISTS (SELECT 1 FROM "AdversarialReviewFindings" f WHERE f."RunId" = r."RunId"));
                    END IF;
                END $$;
                """);

            migrationBuilder.DropColumn(
                name: "FindingsCaptured",
                table: "AdversarialReviewRuns");
        }
    }
}
