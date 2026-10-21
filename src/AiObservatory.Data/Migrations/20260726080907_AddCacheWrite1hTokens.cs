using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCacheWrite1hTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CacheWrite1hTokens",
                table: "UsageEvents",
                type: "bigint",
                nullable: true
            );

            migrationBuilder.AddColumn<long>(
                name: "CacheWrite1hTokens",
                table: "DailyAggregates",
                type: "bigint",
                nullable: false,
                defaultValue: 0L
            );

            migrationBuilder.AddCheckConstraint(
                name: "CK_UsageEvent_CacheWrite1hTokens_WithinCacheWrite",
                table: "UsageEvents",
                sql: "\"CacheWrite1hTokens\" IS NULL OR (\"CacheWrite1hTokens\" >= 0 AND \"CacheWrite1hTokens\" <= COALESCE(\"CacheWriteTokens\", 0))"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_UsageEvent_CacheWrite1hTokens_WithinCacheWrite",
                table: "UsageEvents"
            );

            migrationBuilder.DropColumn(name: "CacheWrite1hTokens", table: "UsageEvents");

            migrationBuilder.DropColumn(name: "CacheWrite1hTokens", table: "DailyAggregates");
        }
    }
}
