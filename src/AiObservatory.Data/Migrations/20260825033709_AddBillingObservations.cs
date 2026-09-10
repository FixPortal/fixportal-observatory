using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBillingObservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "SourceId",
                table: "SpendEntries",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100
            );

            migrationBuilder.CreateTable(
                name: "BillingObservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SourceId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SourceKind = table.Column<string>(type: "text", nullable: false),
                    UsageScope = table.Column<string>(type: "text", nullable: false),
                    CostBasis = table.Column<string>(type: "text", nullable: false),
                    ObservationKey = table.Column<string>(
                        type: "character varying(200)",
                        maxLength: 200,
                        nullable: false
                    ),
                    OccurredOn = table.Column<LocalDate>(type: "date", nullable: false),
                    BillingPeriod = table.Column<string>(type: "text", nullable: true),
                    Service = table.Column<string>(type: "text", nullable: true),
                    Sku = table.Column<string>(type: "text", nullable: true),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    GrossAmount = table.Column<decimal>(type: "numeric", nullable: false),
                    CreditAmount = table.Column<decimal>(type: "numeric", nullable: false),
                    NetAmount = table.Column<decimal>(type: "numeric", nullable: false),
                    RawPayload = table.Column<string>(type: "jsonb", nullable: false),
                    ObservedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingObservations", x => x.Id);
                    table.CheckConstraint(
                        "CK_BillingObservation_Amounts_Balance",
                        "\"GrossAmount\" + \"CreditAmount\" = \"NetAmount\""
                    );
                    table.CheckConstraint("CK_BillingObservation_Billed", "\"CostBasis\" = 'Billed'");
                    table.CheckConstraint("CK_BillingObservation_Currency_Normalized", "\"Currency\" ~ '^[A-Z]{3}$'");
                    table.CheckConstraint(
                        "CK_BillingObservation_ObservationKey_NonBlank",
                        "btrim(\"ObservationKey\") <> '' AND \"ObservationKey\" = btrim(\"ObservationKey\")"
                    );
                    table.CheckConstraint("CK_BillingObservation_ProviderApi", "\"SourceKind\" = 'ProviderApi'");
                    table.CheckConstraint(
                        "CK_BillingObservation_ProviderKey_Normalized",
                        "btrim(\"ProviderKey\") <> '' AND \"ProviderKey\" = btrim(\"ProviderKey\") AND \"ProviderKey\" = lower(\"ProviderKey\")"
                    );
                    table.CheckConstraint(
                        "CK_BillingObservation_SourceId_NonBlank",
                        "btrim(\"SourceId\") <> '' AND \"SourceId\" = btrim(\"SourceId\")"
                    );
                }
            );

            migrationBuilder.InsertData(
                table: "SpendCategories",
                columns: new[] { "Id", "ArchivedAt", "ColorVar", "DisplayName", "Key", "SortOrder" },
                values: new object[]
                {
                    new Guid("11111111-1111-1111-1111-111111111106"),
                    null,
                    "--spend-api-usage",
                    "API Usage",
                    "api-usage",
                    60,
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_BillingObservations_OccurredOn",
                table: "BillingObservations",
                column: "OccurredOn"
            );

            migrationBuilder.CreateIndex(
                name: "IX_BillingObservations_SourceId_ObservationKey",
                table: "BillingObservations",
                columns: new[] { "SourceId", "ObservationKey" },
                unique: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "BillingObservations");

            // The api-usage category is what the billing-observation writer books spend
            // against, and FK_SpendEntries_SpendCategories_CategoryId is Restrict, so once
            // any SpendEntry references it the delete is blocked. Delete it only while it
            // is unreferenced; a referenced category survives the rollback rather than
            // aborting it with a foreign-key violation.
            migrationBuilder.Sql(
                """
                DELETE FROM "SpendCategories"
                WHERE "Id" = '11111111-1111-1111-1111-111111111106'
                    AND NOT EXISTS (
                        SELECT 1 FROM "SpendEntries"
                        WHERE "CategoryId" = '11111111-1111-1111-1111-111111111106'
                    )
                """
            );

            migrationBuilder.AlterColumn<string>(
                name: "SourceId",
                table: "SpendEntries",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200
            );
        }
    }
}
