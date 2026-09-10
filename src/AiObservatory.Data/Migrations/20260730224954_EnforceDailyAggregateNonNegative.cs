using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class EnforceDailyAggregateNonNegative : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "CK_DailyAggregate_CacheReadTokens_NonNegative",
                table: "DailyAggregates",
                sql: "\"CacheReadTokens\" >= 0"
            );

            migrationBuilder.AddCheckConstraint(
                name: "CK_DailyAggregate_CacheWrite1hTokens_WithinCacheWrite",
                table: "DailyAggregates",
                sql: "\"CacheWrite1hTokens\" >= 0 AND \"CacheWrite1hTokens\" <= \"CacheWriteTokens\""
            );

            migrationBuilder.AddCheckConstraint(
                name: "CK_DailyAggregate_CacheWriteTokens_NonNegative",
                table: "DailyAggregates",
                sql: "\"CacheWriteTokens\" >= 0"
            );

            migrationBuilder.AddCheckConstraint(
                name: "CK_DailyAggregate_CostUsd_NonNegative",
                table: "DailyAggregates",
                sql: "\"CostUsd\" >= 0"
            );

            migrationBuilder.AddCheckConstraint(
                name: "CK_DailyAggregate_InputTokens_NonNegative",
                table: "DailyAggregates",
                sql: "\"InputTokens\" >= 0"
            );

            migrationBuilder.AddCheckConstraint(
                name: "CK_DailyAggregate_OutputTokens_NonNegative",
                table: "DailyAggregates",
                sql: "\"OutputTokens\" >= 0"
            );

            migrationBuilder.AddCheckConstraint(
                name: "CK_DailyAggregate_RequestCount_NonNegative",
                table: "DailyAggregates",
                sql: "\"RequestCount\" >= 0"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_DailyAggregate_CacheReadTokens_NonNegative",
                table: "DailyAggregates"
            );

            migrationBuilder.DropCheckConstraint(
                name: "CK_DailyAggregate_CacheWrite1hTokens_WithinCacheWrite",
                table: "DailyAggregates"
            );

            migrationBuilder.DropCheckConstraint(
                name: "CK_DailyAggregate_CacheWriteTokens_NonNegative",
                table: "DailyAggregates"
            );

            migrationBuilder.DropCheckConstraint(
                name: "CK_DailyAggregate_CostUsd_NonNegative",
                table: "DailyAggregates"
            );

            migrationBuilder.DropCheckConstraint(
                name: "CK_DailyAggregate_InputTokens_NonNegative",
                table: "DailyAggregates"
            );

            migrationBuilder.DropCheckConstraint(
                name: "CK_DailyAggregate_OutputTokens_NonNegative",
                table: "DailyAggregates"
            );

            migrationBuilder.DropCheckConstraint(
                name: "CK_DailyAggregate_RequestCount_NonNegative",
                table: "DailyAggregates"
            );
        }
    }
}
