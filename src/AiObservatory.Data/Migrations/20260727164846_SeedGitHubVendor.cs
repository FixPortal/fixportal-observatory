using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <summary>
    /// Adds a plain "GitHub" vendor for everything on the org bill that is not Actions
    /// compute: Advanced Security, Code Quality AI Credits, and whatever GitHub adds next.
    /// <para>
    /// Needed because the GitHub billing sync had nowhere honest to book those lines. The
    /// pre-existing github-actions vendor is named for what it covers, so routing a Code
    /// Quality AI Credit through it would misattribute the charge in every vendor
    /// breakdown — and Code Quality credits are the AI spend on that bill most worth
    /// seeing. Provider stays null: these are dollar charges with no token count behind
    /// them, and inventing one would fabricate a variance comparison that never existed.
    /// </para>
    /// </summary>
    /// <inheritdoc />
    public partial class SeedGitHubVendor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "SpendVendors",
                columns: new[] { "Id", "ArchivedAt", "DefaultCategoryId", "DisplayName", "Key", "Provider" },
                values: new object[]
                {
                    new Guid("22222222-2222-2222-2222-222222222212"),
                    null,
                    new Guid("11111111-1111-1111-1111-111111111104"),
                    "GitHub",
                    "github",
                    null,
                }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // DeleteBehavior.Restrict blocks this once a SpendEntry references the vendor,
            // so the rollback would fail with a foreign-key violation (aborting the
            // migration transaction cleanly). Same reasoning as SeedCopilotVendor and
            // SeedSpendCatalog: fail up front, or archive
            // the vendor through the catalog panel, which is the soft delete it is meant to
            // use. The billing sync writes entries against this vendor on its first run, so
            // in practice it is referenced within a day of deploying.
            throw new NotSupportedException(
                "SeedGitHubVendor cannot be rolled back once spend entries reference the "
                    + "seeded vendor. Roll forward, or archive the vendor instead."
            );
        }
    }
}
