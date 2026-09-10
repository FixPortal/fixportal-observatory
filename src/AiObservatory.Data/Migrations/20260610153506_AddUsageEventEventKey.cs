using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiObservatory.Data.Migrations;

/// <inheritdoc />
public partial class AddUsageEventEventKey : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "EventKey",
            table: "UsageEvents",
            type: "character varying(200)",
            maxLength: 200,
            nullable: true
        );

        migrationBuilder.CreateIndex(
            name: "IX_UsageEvents_EventKey",
            table: "UsageEvents",
            column: "EventKey",
            unique: true,
            filter: "\"EventKey\" IS NOT NULL"
        );
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_UsageEvents_EventKey", table: "UsageEvents");

        migrationBuilder.DropColumn(name: "EventKey", table: "UsageEvents");
    }
}
