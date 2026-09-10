using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGitHubActivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GitHubCommits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Repo = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Sha = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Author = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CommittedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    Additions = table.Column<int>(type: "integer", nullable: false),
                    Deletions = table.Column<int>(type: "integer", nullable: false),
                    IngestedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitHubCommits", x => x.Id);
                    table.CheckConstraint("CK_GitHubCommit_Additions_NonNegative", "\"Additions\" >= 0");
                    table.CheckConstraint("CK_GitHubCommit_Deletions_NonNegative", "\"Deletions\" >= 0");
                }
            );

            migrationBuilder.CreateTable(
                name: "GitHubPullRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Repo = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Author = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    MergedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    ClosedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    FirstReviewAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    ReviewCount = table.Column<int>(type: "integer", nullable: false),
                    IngestedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitHubPullRequests", x => x.Id);
                    table.CheckConstraint("CK_GitHubPullRequest_ReviewCount_NonNegative", "\"ReviewCount\" >= 0");
                }
            );

            migrationBuilder.CreateTable(
                name: "GitHubWorkflowRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Repo = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RunId = table.Column<long>(type: "bigint", nullable: false),
                    WorkflowName = table.Column<string>(
                        type: "character varying(200)",
                        maxLength: 200,
                        nullable: false
                    ),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    IngestedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitHubWorkflowRuns", x => x.Id);
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_GitHubCommits_CommittedAt",
                table: "GitHubCommits",
                column: "CommittedAt"
            );

            migrationBuilder.CreateIndex(
                name: "IX_GitHubCommits_Repo_Sha",
                table: "GitHubCommits",
                columns: new[] { "Repo", "Sha" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_GitHubPullRequests_CreatedAt",
                table: "GitHubPullRequests",
                column: "CreatedAt"
            );

            migrationBuilder.CreateIndex(
                name: "IX_GitHubPullRequests_Repo_Number",
                table: "GitHubPullRequests",
                columns: new[] { "Repo", "Number" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_GitHubWorkflowRuns_CreatedAt",
                table: "GitHubWorkflowRuns",
                column: "CreatedAt"
            );

            migrationBuilder.CreateIndex(
                name: "IX_GitHubWorkflowRuns_Repo_RunId",
                table: "GitHubWorkflowRuns",
                columns: new[] { "Repo", "RunId" },
                unique: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "GitHubCommits");

            migrationBuilder.DropTable(name: "GitHubPullRequests");

            migrationBuilder.DropTable(name: "GitHubWorkflowRuns");
        }
    }
}
