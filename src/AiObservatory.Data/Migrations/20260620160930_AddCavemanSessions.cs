using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCavemanSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CavemanSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    OccurredAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    Mode = table.Column<string>(type: "text", nullable: true),
                    Model = table.Column<string>(type: "text", nullable: true),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    EstSavedTokens = table.Column<long>(type: "bigint", nullable: false),
                    EstSavedUsd = table.Column<decimal>(type: "numeric", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CavemanSessions", x => x.Id);
                    table.CheckConstraint("CK_CavemanSession_EstSavedTokens_NonNegative", "\"EstSavedTokens\" >= 0");
                    table.CheckConstraint("CK_CavemanSession_EstSavedUsd_NonNegative", "\"EstSavedUsd\" >= 0");
                    table.CheckConstraint("CK_CavemanSession_OutputTokens_NonNegative", "\"OutputTokens\" >= 0");
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_CavemanSessions_OccurredAt",
                table: "CavemanSessions",
                column: "OccurredAt"
            );

            migrationBuilder.CreateIndex(
                name: "IX_CavemanSessions_SessionId",
                table: "CavemanSessions",
                column: "SessionId",
                unique: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "CavemanSessions");
        }
    }
}
