using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSpendLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SpendCategories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ColorVar = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    ArchivedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpendCategories", x => x.Id);
                }
            );

            migrationBuilder.CreateTable(
                name: "SpendVendors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Provider = table.Column<string>(type: "text", nullable: true),
                    DefaultCategoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    ArchivedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpendVendors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SpendVendors_SpendCategories_DefaultCategoryId",
                        column: x => x.DefaultCategoryId,
                        principalTable: "SpendCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "SpendEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredOn = table.Column<LocalDate>(type: "date", nullable: false),
                    VendorId = table.Column<Guid>(type: "uuid", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    AmountGbp = table.Column<decimal>(type: "numeric", nullable: false),
                    FxRate = table.Column<decimal>(type: "numeric", nullable: false),
                    Description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Source = table.Column<string>(type: "text", nullable: false),
                    EntryKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RecordedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpendEntries", x => x.Id);
                    table.CheckConstraint("CK_SpendEntry_Amount_NonNegative", "\"Amount\" >= 0");
                    table.CheckConstraint("CK_SpendEntry_AmountGbp_NonNegative", "\"AmountGbp\" >= 0");
                    table.CheckConstraint("CK_SpendEntry_FxRate_Positive", "\"FxRate\" > 0");
                    table.ForeignKey(
                        name: "FK_SpendEntries_SpendCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "SpendCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "FK_SpendEntries_SpendVendors_VendorId",
                        column: x => x.VendorId,
                        principalTable: "SpendVendors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_SpendCategories_Key",
                table: "SpendCategories",
                column: "Key",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_SpendEntries_CategoryId",
                table: "SpendEntries",
                column: "CategoryId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_SpendEntries_OccurredOn",
                table: "SpendEntries",
                column: "OccurredOn"
            );

            migrationBuilder.CreateIndex(
                name: "IX_SpendEntries_Source_EntryKey",
                table: "SpendEntries",
                columns: new[] { "Source", "EntryKey" },
                unique: true,
                filter: "\"EntryKey\" IS NOT NULL"
            );

            migrationBuilder.CreateIndex(name: "IX_SpendEntries_VendorId", table: "SpendEntries", column: "VendorId");

            migrationBuilder.CreateIndex(
                name: "IX_SpendVendors_DefaultCategoryId",
                table: "SpendVendors",
                column: "DefaultCategoryId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_SpendVendors_Key",
                table: "SpendVendors",
                column: "Key",
                unique: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "SpendEntries");

            migrationBuilder.DropTable(name: "SpendVendors");

            migrationBuilder.DropTable(name: "SpendCategories");
        }
    }
}
