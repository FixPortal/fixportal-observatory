using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropIssuesAcceptedUpperBound : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_AdversarialReviewRun_IssuesAccepted_Valid",
                table: "AdversarialReviewRuns"
            );

            migrationBuilder.AddCheckConstraint(
                name: "CK_AdversarialReviewRun_IssuesAccepted_NonNegative",
                table: "AdversarialReviewRuns",
                sql: "\"IssuesAccepted\" >= 0"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not reversible: once rows with IssuesAccepted > IssuesRaised exist
            // (cross-exam credited findings), Postgres rejects re-adding the old
            // upper-bound constraint. Normalise or purge such rows before downgrading.
            throw new InvalidOperationException(
                "DropIssuesAcceptedUpperBound is not reversible. "
                    + "Rows with IssuesAccepted > IssuesRaised may exist; "
                    + "cap or purge them before attempting a downgrade."
            );
        }
    }
}
