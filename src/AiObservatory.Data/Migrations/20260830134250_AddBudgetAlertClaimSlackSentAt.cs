using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBudgetAlertClaimSlackSentAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Instant>(
                name: "SlackSentAt",
                table: "BudgetAlertClaims",
                type: "timestamp with time zone",
                nullable: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "SlackSentAt", table: "BudgetAlertClaims");
        }
    }
}
