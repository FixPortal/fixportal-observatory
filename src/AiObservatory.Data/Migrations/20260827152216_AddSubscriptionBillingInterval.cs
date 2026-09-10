using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptionBillingInterval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BillingInterval",
                table: "Subscriptions",
                type: "text",
                nullable: false,
                defaultValue: "Monthly"
            );

            migrationBuilder.AddColumn<int>(
                name: "BillingMonth",
                table: "Subscriptions",
                type: "integer",
                nullable: true
            );

            migrationBuilder.AddCheckConstraint(
                name: "CK_Subscription_BillingMonth_Valid",
                table: "Subscriptions",
                sql: "(\"BillingInterval\" = 'Monthly' AND \"BillingMonth\" IS NULL) OR (\"BillingInterval\" = 'Annual' AND \"BillingMonth\" IS NOT NULL AND \"BillingMonth\" BETWEEN 1 AND 12)"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(name: "CK_Subscription_BillingMonth_Valid", table: "Subscriptions");

            migrationBuilder.DropColumn(name: "BillingInterval", table: "Subscriptions");

            migrationBuilder.DropColumn(name: "BillingMonth", table: "Subscriptions");
        }
    }
}
