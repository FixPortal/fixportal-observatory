using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAdversarialReviewRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdversarialReviewRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Reviewer = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    InputTokens = table.Column<long>(type: "bigint", nullable: false),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    CostUsd = table.Column<decimal>(type: "numeric", nullable: false),
                    ReviewDurationMs = table.Column<long>(type: "bigint", nullable: false),
                    IssuesRaised = table.Column<int>(type: "integer", nullable: false),
                    IssuesAccepted = table.Column<int>(type: "integer", nullable: false),
                    CostPerAcceptedFinding = table.Column<decimal>(type: "numeric", nullable: true),
                    RunId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RecordedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdversarialReviewRuns", x => x.Id);
                    table.CheckConstraint("CK_AdversarialReviewRun_CostUsd_NonNegative", "\"CostUsd\" >= 0");
                    table.CheckConstraint("CK_AdversarialReviewRun_InputTokens_NonNegative", "\"InputTokens\" >= 0");
                    table.CheckConstraint(
                        "CK_AdversarialReviewRun_IssuesAccepted_Valid",
                        "\"IssuesAccepted\" >= 0 AND \"IssuesAccepted\" <= \"IssuesRaised\""
                    );
                    table.CheckConstraint("CK_AdversarialReviewRun_IssuesRaised_NonNegative", "\"IssuesRaised\" >= 0");
                    table.CheckConstraint("CK_AdversarialReviewRun_OutputTokens_NonNegative", "\"OutputTokens\" >= 0");
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_AdversarialReviewRuns_RecordedAt",
                table: "AdversarialReviewRuns",
                column: "RecordedAt"
            );

            migrationBuilder.CreateIndex(
                name: "IX_AdversarialReviewRuns_Reviewer_Model",
                table: "AdversarialReviewRuns",
                columns: new[] { "Reviewer", "Model" }
            );

            migrationBuilder.CreateIndex(
                name: "IX_AdversarialReviewRuns_RunId",
                table: "AdversarialReviewRuns",
                column: "RunId",
                unique: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "AdversarialReviewRuns");
        }
    }
}
