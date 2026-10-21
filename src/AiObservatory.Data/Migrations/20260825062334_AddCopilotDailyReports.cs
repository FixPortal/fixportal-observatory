using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCopilotDailyReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CopilotDailyReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Day = table.Column<LocalDate>(type: "date", nullable: false),
                    SourceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SourceKind = table.Column<string>(type: "text", nullable: false),
                    UsageScope = table.Column<string>(type: "text", nullable: false),
                    CostBasis = table.Column<string>(type: "text", nullable: false),
                    ReportKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DailyActiveUsers = table.Column<int>(type: "integer", nullable: true),
                    WeeklyActiveUsers = table.Column<int>(type: "integer", nullable: true),
                    MonthlyActiveUsers = table.Column<int>(type: "integer", nullable: true),
                    UserInitiatedInteractionCount = table.Column<long>(type: "bigint", nullable: false),
                    CodeGenerationActivityCount = table.Column<long>(type: "bigint", nullable: false),
                    CodeAcceptanceActivityCount = table.Column<long>(type: "bigint", nullable: false),
                    RawPayload = table.Column<string>(type: "jsonb", nullable: false),
                    ObservedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CopilotDailyReports", x => x.Id);
                    table.CheckConstraint(
                        "CK_CopilotDailyReport_CodeAcceptanceActivityCount_NonNegative",
                        "\"CodeAcceptanceActivityCount\" >= 0"
                    );
                    table.CheckConstraint(
                        "CK_CopilotDailyReport_CodeGenerationActivityCount_NonNegative",
                        "\"CodeGenerationActivityCount\" >= 0"
                    );
                    table.CheckConstraint(
                        "CK_CopilotDailyReport_DailyActiveUsers_NonNegative",
                        "\"DailyActiveUsers\" IS NULL OR \"DailyActiveUsers\" >= 0"
                    );
                    table.CheckConstraint(
                        "CK_CopilotDailyReport_MonthlyActiveUsers_NonNegative",
                        "\"MonthlyActiveUsers\" IS NULL OR \"MonthlyActiveUsers\" >= 0"
                    );
                    table.CheckConstraint("CK_CopilotDailyReport_NoCost", "\"CostBasis\" = 'None'");
                    table.CheckConstraint("CK_CopilotDailyReport_ProviderApi", "\"SourceKind\" = 'ProviderApi'");
                    table.CheckConstraint("CK_CopilotDailyReport_Subscription", "\"UsageScope\" = 'Subscription'");
                    table.CheckConstraint(
                        "CK_CopilotDailyReport_UserInitiatedInteractionCount_NonNegative",
                        "\"UserInitiatedInteractionCount\" >= 0"
                    );
                    table.CheckConstraint(
                        "CK_CopilotDailyReport_WeeklyActiveUsers_NonNegative",
                        "\"WeeklyActiveUsers\" IS NULL OR \"WeeklyActiveUsers\" >= 0"
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_CopilotDailyReports_Day",
                table: "CopilotDailyReports",
                column: "Day"
            );

            migrationBuilder.CreateIndex(
                name: "IX_CopilotDailyReports_SourceId_ReportKey",
                table: "CopilotDailyReports",
                columns: new[] { "SourceId", "ReportKey" },
                unique: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "CopilotDailyReports");
        }
    }
}
