using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <summary>
    /// Lets a refund or credit be recorded as a negative amount, replacing the two
    /// non-negative check constraints with a non-zero one. Feeding only debits overstated
    /// billed spend by the refund total, which is the same class of silent overstatement
    /// the token-pricing correction existed to fix, arriving from the other direction.
    /// See SpendEntry.Amount for why this beat an IsRefund flag.
    /// </summary>
    /// <inheritdoc />
    public partial class AllowNegativeSpendAmounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(name: "CK_SpendEntry_Amount_NonNegative", table: "SpendEntries");

            migrationBuilder.DropCheckConstraint(name: "CK_SpendEntry_AmountGbp_NonNegative", table: "SpendEntries");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SpendEntry_Amount_NonZero",
                table: "SpendEntries",
                sql: "\"Amount\" <> 0"
            );
        }

        /// <remarks>
        /// Rolls back cleanly only while no refund has been recorded. Once one has,
        /// PostgreSQL refuses to re-add a constraint the existing rows violate and the
        /// rollback stops here, which is the correct outcome — silently discarding the sign
        /// would understate refunds instead. Delete the negative rows first if the rollback
        /// is genuinely wanted. This is deliberately a hard failure rather than the
        /// unconditional throw SeedSpendCatalog.Down uses: unlike that one, this rollback is
        /// valid whenever the ledger holds charges only.
        /// </remarks>
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(name: "CK_SpendEntry_Amount_NonZero", table: "SpendEntries");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SpendEntry_Amount_NonNegative",
                table: "SpendEntries",
                sql: "\"Amount\" >= 0"
            );

            migrationBuilder.AddCheckConstraint(
                name: "CK_SpendEntry_AmountGbp_NonNegative",
                table: "SpendEntries",
                sql: "\"AmountGbp\" >= 0"
            );
        }
    }
}
