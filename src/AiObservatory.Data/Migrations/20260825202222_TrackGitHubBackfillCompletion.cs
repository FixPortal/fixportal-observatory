using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class TrackGitHubBackfillCompletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GitHubBackfillStates",
                columns: table => new
                {
                    Repo = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    HasPullRequests = table.Column<bool>(type: "boolean", nullable: false),
                    HasCommits = table.Column<bool>(type: "boolean", nullable: false),
                    HasWorkflowRuns = table.Column<bool>(type: "boolean", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitHubBackfillStates", x => x.Repo);
                }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "GitHubBackfillStates");
        }
    }
}
