using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceSyncStates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SourceSyncStates",
                columns: table => new
                {
                    SourceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsConfigured = table.Column<bool>(type: "boolean", nullable: false),
                    IsAvailable = table.Column<bool>(type: "boolean", nullable: true),
                    ExpectedRefreshIntervalSeconds = table.Column<long>(type: "bigint", nullable: false),
                    LastAttemptAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    LastSuccessAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    LatestObservationAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    ConsecutiveFailureCount = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceSyncStates", x => x.SourceId);
                }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "SourceSyncStates");
        }
    }
}
