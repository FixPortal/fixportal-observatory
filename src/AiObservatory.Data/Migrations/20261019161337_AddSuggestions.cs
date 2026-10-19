using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSuggestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SuggestionRuleRuns",
                columns: table => new
                {
                    RuleId = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    EvaluatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SubjectsEvaluated = table.Column<int>(type: "integer", nullable: false),
                    SuggestionsEmitted = table.Column<int>(type: "integer", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SuggestionRuleRuns", x => x.RuleId);
                }
            );

            migrationBuilder.CreateTable(
                name: "Suggestions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    RuleId = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    EvidenceJson = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SnoozedUntil = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    FirstSeenAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    LastEvaluatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    BaselineFiguresJson = table.Column<string>(type: "jsonb", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Suggestions", x => x.Id);
                }
            );

            migrationBuilder.CreateIndex(name: "IX_Suggestions_Key", table: "Suggestions", column: "Key", unique: true);

            migrationBuilder.CreateIndex(name: "IX_Suggestions_Status", table: "Suggestions", column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "SuggestionRuleRuns");

            migrationBuilder.DropTable(name: "Suggestions");
        }
    }
}
