using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiObservatory.Data.Migrations;

/// <inheritdoc />
public partial class FixEventKeyIndexCompound : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_UsageEvents_EventKey", table: "UsageEvents");

        migrationBuilder.AlterColumn<long>(
            name: "OutputTokens",
            table: "UsageEvents",
            type: "bigint",
            nullable: false,
            oldClrType: typeof(int),
            oldType: "integer"
        );

        migrationBuilder.AlterColumn<long>(
            name: "InputTokens",
            table: "UsageEvents",
            type: "bigint",
            nullable: false,
            oldClrType: typeof(int),
            oldType: "integer"
        );

        migrationBuilder.AlterColumn<long>(
            name: "CacheWriteTokens",
            table: "UsageEvents",
            type: "bigint",
            nullable: true,
            oldClrType: typeof(int),
            oldType: "integer",
            oldNullable: true
        );

        migrationBuilder.AlterColumn<long>(
            name: "CacheReadTokens",
            table: "UsageEvents",
            type: "bigint",
            nullable: true,
            oldClrType: typeof(int),
            oldType: "integer",
            oldNullable: true
        );

        migrationBuilder.AddColumn<long>(
            name: "CacheReadTokens",
            table: "DailyAggregates",
            type: "bigint",
            nullable: false,
            defaultValue: 0L
        );

        migrationBuilder.AddColumn<long>(
            name: "CacheWriteTokens",
            table: "DailyAggregates",
            type: "bigint",
            nullable: false,
            defaultValue: 0L
        );

        migrationBuilder.CreateIndex(name: "IX_UsageEvents_OccurredAt", table: "UsageEvents", column: "OccurredAt");

        migrationBuilder.CreateIndex(
            name: "IX_UsageEvents_Provider_EventKey",
            table: "UsageEvents",
            columns: new[] { "Provider", "EventKey" },
            unique: true,
            filter: "\"EventKey\" IS NOT NULL"
        );

        migrationBuilder.AddCheckConstraint(
            name: "CK_UsageEvent_CacheReadTokens_NonNegative",
            table: "UsageEvents",
            sql: "\"CacheReadTokens\" IS NULL OR \"CacheReadTokens\" >= 0"
        );

        migrationBuilder.AddCheckConstraint(
            name: "CK_UsageEvent_CacheWriteTokens_NonNegative",
            table: "UsageEvents",
            sql: "\"CacheWriteTokens\" IS NULL OR \"CacheWriteTokens\" >= 0"
        );

        migrationBuilder.AddCheckConstraint(
            name: "CK_UsageEvent_CostUsd_NonNegative",
            table: "UsageEvents",
            sql: "\"CostUsd\" >= 0"
        );

        migrationBuilder.AddCheckConstraint(
            name: "CK_UsageEvent_InputTokens_NonNegative",
            table: "UsageEvents",
            sql: "\"InputTokens\" >= 0"
        );

        migrationBuilder.AddCheckConstraint(
            name: "CK_UsageEvent_OutputTokens_NonNegative",
            table: "UsageEvents",
            sql: "\"OutputTokens\" >= 0"
        );
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_UsageEvents_OccurredAt", table: "UsageEvents");

        migrationBuilder.DropIndex(name: "IX_UsageEvents_Provider_EventKey", table: "UsageEvents");

        migrationBuilder.DropCheckConstraint(name: "CK_UsageEvent_CacheReadTokens_NonNegative", table: "UsageEvents");

        migrationBuilder.DropCheckConstraint(name: "CK_UsageEvent_CacheWriteTokens_NonNegative", table: "UsageEvents");

        migrationBuilder.DropCheckConstraint(name: "CK_UsageEvent_CostUsd_NonNegative", table: "UsageEvents");

        migrationBuilder.DropCheckConstraint(name: "CK_UsageEvent_InputTokens_NonNegative", table: "UsageEvents");

        migrationBuilder.DropCheckConstraint(name: "CK_UsageEvent_OutputTokens_NonNegative", table: "UsageEvents");

        migrationBuilder.DropColumn(name: "CacheReadTokens", table: "DailyAggregates");

        migrationBuilder.DropColumn(name: "CacheWriteTokens", table: "DailyAggregates");

        migrationBuilder.AlterColumn<int>(
            name: "OutputTokens",
            table: "UsageEvents",
            type: "integer",
            nullable: false,
            oldClrType: typeof(long),
            oldType: "bigint"
        );

        migrationBuilder.AlterColumn<int>(
            name: "InputTokens",
            table: "UsageEvents",
            type: "integer",
            nullable: false,
            oldClrType: typeof(long),
            oldType: "bigint"
        );

        migrationBuilder.AlterColumn<int>(
            name: "CacheWriteTokens",
            table: "UsageEvents",
            type: "integer",
            nullable: true,
            oldClrType: typeof(long),
            oldType: "bigint",
            oldNullable: true
        );

        migrationBuilder.AlterColumn<int>(
            name: "CacheReadTokens",
            table: "UsageEvents",
            type: "integer",
            nullable: true,
            oldClrType: typeof(long),
            oldType: "bigint",
            oldNullable: true
        );

        migrationBuilder.CreateIndex(
            name: "IX_UsageEvents_EventKey",
            table: "UsageEvents",
            column: "EventKey",
            unique: true,
            filter: "\"EventKey\" IS NOT NULL"
        );
    }
}
