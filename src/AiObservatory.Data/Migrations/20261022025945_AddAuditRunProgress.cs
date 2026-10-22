using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditRunProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PartialEvidenceAcknowledged",
                table: "AuditRuns",
                type: "boolean",
                nullable: false,
                defaultValue: false
            );
            migrationBuilder.AddColumn<Guid[]>(
                name: "CaptureRequestIds",
                table: "AuditRuns",
                type: "uuid[]",
                nullable: false,
                defaultValueSql: "'{}'::uuid[]"
            );

            migrationBuilder.AddColumn<string>(
                name: "Stage",
                table: "AuditRuns",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "queued"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "PartialEvidenceAcknowledged", table: "AuditRuns");
            migrationBuilder.DropColumn(name: "CaptureRequestIds", table: "AuditRuns");

            migrationBuilder.DropColumn(name: "Stage", table: "AuditRuns");
        }
    }
}
