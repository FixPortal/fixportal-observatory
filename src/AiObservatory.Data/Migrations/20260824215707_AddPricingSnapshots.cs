using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPricingSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PricingSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "text", nullable: false),
                    SourceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RetrievedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    SourceUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RawEvidence = table.Column<string>(type: "text", nullable: false),
                    NormalizedCatalog = table.Column<string>(type: "jsonb", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PricingSnapshots", x => x.Id);
                    table.CheckConstraint("CK_PricingSnapshot_ContentHash_Length", "char_length(\"ContentHash\") = 64");
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_PricingSnapshots_SourceId",
                table: "PricingSnapshots",
                column: "SourceId",
                unique: true,
                filter: "\"IsActive\""
            );

            migrationBuilder.CreateIndex(
                name: "IX_PricingSnapshots_SourceId_ContentHash",
                table: "PricingSnapshots",
                columns: new[] { "SourceId", "ContentHash" },
                unique: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "PricingSnapshots");
        }
    }
}
