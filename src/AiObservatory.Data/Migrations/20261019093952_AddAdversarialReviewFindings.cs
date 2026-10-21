using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAdversarialReviewFindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdversarialReviewFindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ChunkId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    FindingId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RecordedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdversarialReviewFindings", x => x.Id);
                }
            );

            migrationBuilder.CreateTable(
                name: "AdversarialReviewFindingParticipants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FindingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reviewer = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Verdict = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdversarialReviewFindingParticipants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AdversarialReviewFindingParticipants_AdversarialReviewFindi~",
                        column: x => x.FindingId,
                        principalTable: "AdversarialReviewFindings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_AdversarialReviewFindingParticipants_FindingId",
                table: "AdversarialReviewFindingParticipants",
                column: "FindingId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_AdversarialReviewFindingParticipants_Reviewer_Model",
                table: "AdversarialReviewFindingParticipants",
                columns: new[] { "Reviewer", "Model" }
            );

            migrationBuilder.CreateIndex(
                name: "IX_AdversarialReviewFindings_RunId_ChunkId_FindingId",
                table: "AdversarialReviewFindings",
                columns: new[] { "RunId", "ChunkId", "FindingId" },
                unique: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "AdversarialReviewFindingParticipants");

            migrationBuilder.DropTable(name: "AdversarialReviewFindings");
        }
    }
}
