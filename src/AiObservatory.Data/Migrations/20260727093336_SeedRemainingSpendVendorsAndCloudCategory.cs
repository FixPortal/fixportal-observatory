using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace AiObservatory.Data.Migrations
{
    /// <summary>
    /// Adds the five vendors that carry real billed spend but had no row to record it
    /// against — OpenAI, Google, Microsoft, OpenRouter and Blacksmith — plus a Cloud
    /// category for infrastructure spend, which has no token estimate behind it and so
    /// would distort Subscription if folded in there.
    /// </summary>
    /// <inheritdoc />
    public partial class SeedRemainingSpendVendorsAndCloudCategory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "SpendCategories",
                columns: new[] { "Id", "ArchivedAt", "ColorVar", "DisplayName", "Key", "SortOrder" },
                values: new object[]
                {
                    new Guid("11111111-1111-1111-1111-111111111105"),
                    null,
                    "--spend-cloud",
                    "Cloud",
                    "cloud",
                    50,
                }
            );

            migrationBuilder.InsertData(
                table: "SpendVendors",
                columns: new[] { "Id", "ArchivedAt", "DefaultCategoryId", "DisplayName", "Key", "Provider" },
                values: new object[,]
                {
                    {
                        new Guid("22222222-2222-2222-2222-222222222206"),
                        null,
                        new Guid("11111111-1111-1111-1111-111111111104"),
                        "OpenAI",
                        "openai",
                        "OpenAI",
                    },
                    {
                        new Guid("22222222-2222-2222-2222-222222222207"),
                        null,
                        new Guid("11111111-1111-1111-1111-111111111104"),
                        "Google",
                        "google",
                        "Google",
                    },
                    {
                        new Guid("22222222-2222-2222-2222-222222222209"),
                        null,
                        new Guid("11111111-1111-1111-1111-111111111104"),
                        "OpenRouter",
                        "openrouter",
                        null,
                    },
                    {
                        new Guid("22222222-2222-2222-2222-222222222210"),
                        null,
                        new Guid("11111111-1111-1111-1111-111111111103"),
                        "Blacksmith",
                        "blacksmith",
                        null,
                    },
                    {
                        new Guid("22222222-2222-2222-2222-222222222208"),
                        null,
                        new Guid("11111111-1111-1111-1111-111111111105"),
                        "Microsoft",
                        "microsoft",
                        null,
                    },
                }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Same trap SeedSpendCatalog.Down would hit: once any SpendEntry references one
            // of these vendors, DeleteBehavior.Restrict blocks that DeleteData and the
            // rollback fails with a foreign-key violation (aborting the migration
            // transaction cleanly). Fail up front with an actionable message instead. Roll
            // forward, or archive the vendor through the catalog panel — which is the soft
            // delete these rows are actually meant to use.
            throw new NotSupportedException(
                "SeedRemainingSpendVendorsAndCloudCategory cannot be rolled back once spend entries "
                    + "reference the seeded rows. Roll forward, or archive the vendor/category instead."
            );
        }
    }
}
