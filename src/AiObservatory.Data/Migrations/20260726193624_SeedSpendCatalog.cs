using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class SeedSpendCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "SpendCategories",
                columns: new[] { "Id", "ArchivedAt", "ColorVar", "DisplayName", "Key", "SortOrder" },
                values: new object[,]
                {
                    {
                        new Guid("11111111-1111-1111-1111-111111111101"),
                        null,
                        "--spend-code-review",
                        "Code Review",
                        "code-review",
                        10,
                    },
                    {
                        new Guid("11111111-1111-1111-1111-111111111102"),
                        null,
                        "--spend-credits",
                        "Credits",
                        "credits",
                        20,
                    },
                    { new Guid("11111111-1111-1111-1111-111111111103"), null, "--spend-ci", "CI", "ci", 30 },
                    {
                        new Guid("11111111-1111-1111-1111-111111111104"),
                        null,
                        "--spend-subscription",
                        "Subscription",
                        "subscription",
                        40,
                    },
                }
            );

            migrationBuilder.InsertData(
                table: "SpendVendors",
                columns: new[] { "Id", "ArchivedAt", "DefaultCategoryId", "DisplayName", "Key", "Provider" },
                values: new object[,]
                {
                    {
                        new Guid("22222222-2222-2222-2222-222222222201"),
                        null,
                        new Guid("11111111-1111-1111-1111-111111111102"),
                        "Anthropic",
                        "anthropic",
                        "Anthropic",
                    },
                    {
                        new Guid("22222222-2222-2222-2222-222222222202"),
                        null,
                        new Guid("11111111-1111-1111-1111-111111111103"),
                        "GitHub Actions",
                        "github-actions",
                        null,
                    },
                    {
                        new Guid("22222222-2222-2222-2222-222222222203"),
                        null,
                        new Guid("11111111-1111-1111-1111-111111111101"),
                        "CodeRabbit",
                        "coderabbit",
                        null,
                    },
                    {
                        new Guid("22222222-2222-2222-2222-222222222204"),
                        null,
                        new Guid("11111111-1111-1111-1111-111111111101"),
                        "Gitar",
                        "gitar",
                        null,
                    },
                    {
                        new Guid("22222222-2222-2222-2222-222222222205"),
                        null,
                        new Guid("11111111-1111-1111-1111-111111111104"),
                        "Moonshot",
                        "moonshot",
                        "Moonshot",
                    },
                }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Once any SpendEntry references a seeded vendor, DeleteBehavior.Restrict blocks
            // this DeleteData and the rollback fails with a foreign-key violation (aborting
            // the migration transaction cleanly). Fail loudly up front with an actionable
            // message instead.
            throw new NotSupportedException(
                "SeedSpendCatalog cannot be rolled back once spend entries reference the seeded rows. "
                    + "Roll forward, or drop the spend tables via AddSpendLedger's Down."
            );
        }
    }
}
