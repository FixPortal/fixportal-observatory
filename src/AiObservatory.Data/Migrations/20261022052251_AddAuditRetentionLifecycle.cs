using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditRetentionLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "SanitizedSummary",
                table: "AuditRuns",
                type: "bytea",
                nullable: true
            );

            migrationBuilder.AddColumn<Instant>(
                name: "ServerCutoff",
                table: "AuditRuns",
                type: "timestamp with time zone",
                nullable: true
            );

            migrationBuilder.CreateTable(
                name: "AuditExportReadLeases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstanceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ExpiresAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    Cancelled = table.Column<bool>(type: "boolean", nullable: false),
                    ReservedBytes = table.Column<long>(type: "bigint", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditExportReadLeases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuditExportReadLeases_AuditRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "AuditRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_AuditExportReadLeases_InstanceId_ExpiresAt",
                table: "AuditExportReadLeases",
                columns: new[] { "InstanceId", "ExpiresAt" }
            );

            migrationBuilder.CreateIndex(
                name: "IX_AuditExportReadLeases_RunId",
                table: "AuditExportReadLeases",
                column: "RunId"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "AuditExportReadLeases");

            migrationBuilder.DropColumn(name: "SanitizedSummary", table: "AuditRuns");

            migrationBuilder.DropColumn(name: "ServerCutoff", table: "AuditRuns");
        }
    }
}
