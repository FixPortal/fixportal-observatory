using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAdversarialReviewRoleAndRepo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_AdversarialReviewRuns_RunId", table: "AdversarialReviewRuns");

            migrationBuilder.AddColumn<string>(
                name: "Repo",
                table: "AdversarialReviewRuns",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "Role",
                table: "AdversarialReviewRuns",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "reviewer"
            );

            migrationBuilder.CreateIndex(
                name: "IX_AdversarialReviewRuns_RunId_Reviewer_Role",
                table: "AdversarialReviewRuns",
                columns: new[] { "RunId", "Reviewer", "Role" },
                unique: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AdversarialReviewRuns_RunId_Reviewer_Role",
                table: "AdversarialReviewRuns"
            );

            migrationBuilder.DropColumn(name: "Repo", table: "AdversarialReviewRuns");

            migrationBuilder.DropColumn(name: "Role", table: "AdversarialReviewRuns");

            migrationBuilder.CreateIndex(
                name: "IX_AdversarialReviewRuns_RunId",
                table: "AdversarialReviewRuns",
                column: "RunId",
                unique: true
            );
        }
    }
}
