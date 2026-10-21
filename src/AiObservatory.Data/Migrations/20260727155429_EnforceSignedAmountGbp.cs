using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <summary>
    /// Extends the signed-amount invariant to AmountGbp, which AllowNegativeSpendAmounts
    /// left unconstrained. That column is the one every total sums, so a zero or
    /// opposite-sign value there flips a refund into a charge across every aggregate at
    /// once and invisibly — a worse failure than the same mistake on Amount.
    /// <para>
    /// Adding this to a database that already holds a violating row will fail loudly, which
    /// is the intended outcome: silently deleting or rewriting a financial row to satisfy a
    /// constraint would be the more dangerous fix. No such row exists in production
    /// (verified 2026-07-27 — the ledger holds zero entries).
    /// </para>
    /// </summary>
    /// <inheritdoc />
    public partial class EnforceSignedAmountGbp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "CK_SpendEntry_AmountGbp_SameSign",
                table: "SpendEntries",
                sql: "\"Amount\" * \"AmountGbp\" > 0"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(name: "CK_SpendEntry_AmountGbp_SameSign", table: "SpendEntries");
        }
    }
}
