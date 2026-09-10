using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <summary>
    /// Adds GitHub Copilot as a vendor in its own right, separate from GitHub Actions.
    /// The ingest worker has recorded Copilot token usage since 2026-05-26, but with no
    /// vendor to book the charge against it was the one metered provider whose billed
    /// spend could never be compared with its own estimate. Kept apart from
    /// github-actions because only Copilot carries a Provider, so only Copilot can join
    /// to the token pipeline — folding the two together would destroy that link.
    /// </summary>
    /// <inheritdoc />
    public partial class SeedCopilotVendor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "SpendVendors",
                columns: new[] { "Id", "ArchivedAt", "DefaultCategoryId", "DisplayName", "Key", "Provider" },
                values: new object[]
                {
                    new Guid("22222222-2222-2222-2222-222222222211"),
                    null,
                    new Guid("11111111-1111-1111-1111-111111111104"),
                    "GitHub Copilot",
                    "copilot",
                    "Copilot",
                }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // DeleteBehavior.Restrict blocks this once a SpendEntry references the vendor,
            // so the rollback would fail with a foreign-key violation (aborting the
            // migration transaction cleanly). Same reasoning as SeedSpendCatalog
            // and SeedRemainingSpendVendorsAndCloudCategory: fail up front, or archive the
            // vendor through the catalog panel, which is the soft delete it is meant to use.
            throw new NotSupportedException(
                "SeedCopilotVendor cannot be rolled back once spend entries reference the "
                    + "seeded vendor. Roll forward, or archive the vendor instead."
            );
        }
    }
}
