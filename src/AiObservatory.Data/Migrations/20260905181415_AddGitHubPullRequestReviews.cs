using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGitHubPullRequestReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GitHubPullRequestReviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Repo = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    ReviewId = table.Column<long>(type: "bigint", nullable: false),
                    Reviewer = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IsBot = table.Column<bool>(type: "boolean", nullable: false),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SubmittedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    IngestedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitHubPullRequestReviews", x => x.Id);
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_GitHubPullRequestReviews_Repo_Number",
                table: "GitHubPullRequestReviews",
                columns: new[] { "Repo", "Number" }
            );

            migrationBuilder.CreateIndex(
                name: "IX_GitHubPullRequestReviews_Repo_ReviewId",
                table: "GitHubPullRequestReviews",
                columns: new[] { "Repo", "ReviewId" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_GitHubPullRequestReviews_SubmittedAt",
                table: "GitHubPullRequestReviews",
                column: "SubmittedAt"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "GitHubPullRequestReviews");
        }
    }
}
