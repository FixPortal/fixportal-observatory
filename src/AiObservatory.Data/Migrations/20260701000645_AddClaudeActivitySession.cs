using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddClaudeActivitySession : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClaudeActivitySessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Project = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    StartedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ActiveSeconds = table.Column<long>(type: "bigint", nullable: false),
                    IngestedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClaudeActivitySessions", x => x.Id);
                    table.CheckConstraint(
                        "CK_ClaudeActivitySession_ActiveSeconds_NonNegative",
                        "\"ActiveSeconds\" >= 0"
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_ClaudeActivitySessions_Project",
                table: "ClaudeActivitySessions",
                column: "Project"
            );

            migrationBuilder.CreateIndex(
                name: "IX_ClaudeActivitySessions_SessionId",
                table: "ClaudeActivitySessions",
                column: "SessionId",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_ClaudeActivitySessions_StartedAt",
                table: "ClaudeActivitySessions",
                column: "StartedAt"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ClaudeActivitySessions");
        }
    }
}
