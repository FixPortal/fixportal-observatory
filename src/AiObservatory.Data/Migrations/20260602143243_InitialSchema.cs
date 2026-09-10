using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace AiObservatory.Data.Migrations;

/// <inheritdoc />
public partial class InitialSchema : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "BudgetRules",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Provider = table.Column<string>(type: "text", nullable: true),
                Period = table.Column<string>(type: "text", nullable: false),
                ThresholdUsd = table.Column<decimal>(type: "numeric", nullable: false),
                LastTriggeredAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_BudgetRules", x => x.Id);
            }
        );

        migrationBuilder.CreateTable(
            name: "DailyAggregates",
            columns: table => new
            {
                Date = table.Column<LocalDate>(type: "date", nullable: false),
                Provider = table.Column<string>(type: "text", nullable: false),
                Model = table.Column<string>(type: "text", nullable: false),
                InputTokens = table.Column<long>(type: "bigint", nullable: false),
                OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                CostUsd = table.Column<decimal>(type: "numeric", nullable: false),
                RequestCount = table.Column<int>(type: "integer", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_DailyAggregates",
                    x => new
                    {
                        x.Date,
                        x.Provider,
                        x.Model,
                    }
                );
            }
        );

        migrationBuilder.CreateTable(
            name: "Insights",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                GeneratedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                PeriodStart = table.Column<LocalDate>(type: "date", nullable: false),
                PeriodEnd = table.Column<LocalDate>(type: "date", nullable: false),
                InsightType = table.Column<string>(type: "text", nullable: false),
                Title = table.Column<string>(type: "text", nullable: false),
                Body = table.Column<string>(type: "text", nullable: false),
                Data = table.Column<string>(type: "jsonb", nullable: false),
                AcknowledgedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Insights", x => x.Id);
            }
        );

        migrationBuilder.CreateTable(
            name: "Subscriptions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Provider = table.Column<string>(type: "text", nullable: false),
                Name = table.Column<string>(type: "text", nullable: false),
                CostAmount = table.Column<decimal>(type: "numeric", nullable: false),
                Currency = table.Column<string>(type: "text", nullable: false),
                BillingDay = table.Column<int>(type: "integer", nullable: false),
                ActiveFrom = table.Column<LocalDate>(type: "date", nullable: false),
                ActiveTo = table.Column<LocalDate>(type: "date", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Subscriptions", x => x.Id);
            }
        );

        migrationBuilder.CreateTable(
            name: "UsageEvents",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Provider = table.Column<string>(type: "text", nullable: false),
                OccurredAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                IngestedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                Model = table.Column<string>(type: "text", nullable: true),
                InputTokens = table.Column<int>(type: "integer", nullable: false),
                OutputTokens = table.Column<int>(type: "integer", nullable: false),
                CacheReadTokens = table.Column<int>(type: "integer", nullable: true),
                CacheWriteTokens = table.Column<int>(type: "integer", nullable: true),
                CostUsd = table.Column<decimal>(type: "numeric", nullable: false),
                RawPayload = table.Column<string>(type: "jsonb", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UsageEvents", x => x.Id);
            }
        );

        migrationBuilder.CreateIndex(
            name: "IX_UsageEvents_Provider_Model",
            table: "UsageEvents",
            columns: new[] { "Provider", "Model" },
            filter: "\"Model\" IS NOT NULL"
        );
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "BudgetRules");

        migrationBuilder.DropTable(name: "DailyAggregates");

        migrationBuilder.DropTable(name: "Insights");

        migrationBuilder.DropTable(name: "Subscriptions");

        migrationBuilder.DropTable(name: "UsageEvents");
    }
}
