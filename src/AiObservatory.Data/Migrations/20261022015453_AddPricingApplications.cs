using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPricingApplications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CurrentPricingRevision",
                table: "UsageEvents",
                type: "bigint",
                nullable: false,
                defaultValue: 0L
            );

            migrationBuilder.CreateTable(
                name: "PricingApplications",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    RevisionDigest = table.Column<string>(
                        type: "character varying(64)",
                        maxLength: 64,
                        nullable: false
                    ),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SnapshotId = table.Column<Guid>(type: "uuid", nullable: true),
                    SnapshotHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RateKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Dimensions = table.Column<byte[]>(type: "bytea", nullable: false),
                    PricingDate = table.Column<DateOnly>(type: "date", nullable: false),
                    RuleVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CostUsd = table.Column<decimal>(type: "numeric", nullable: true),
                    CacheSavingsUsd = table.Column<decimal>(type: "numeric", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PricingApplications", x => new { x.EventId, x.Revision });
                    table.CheckConstraint("CK_PricingApplication_Revision", "\"Revision\" > 0");
                    table.ForeignKey(
                        name: "FK_PricingApplications_PricingSnapshots_SnapshotId",
                        column: x => x.SnapshotId,
                        principalTable: "PricingSnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "FK_PricingApplications_UsageEvents_EventId",
                        column: x => x.EventId,
                        principalTable: "UsageEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_PricingApplications_SnapshotId",
                table: "PricingApplications",
                column: "SnapshotId"
            );
            migrationBuilder.Sql(
                """
                CREATE FUNCTION audit_pricing_application_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'UPDATE' OR EXISTS (SELECT 1 FROM "UsageEvents" WHERE "Id" = OLD."EventId") THEN
                        RAISE EXCEPTION 'pricing_application_immutable' USING ERRCODE = '23514';
                    END IF;
                    RETURN OLD;
                END $$;
                CREATE TRIGGER pricing_application_immutable BEFORE UPDATE OR DELETE ON "PricingApplications"
                FOR EACH ROW EXECUTE FUNCTION audit_pricing_application_immutable();
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "PricingApplications");

            migrationBuilder.Sql("DROP FUNCTION audit_pricing_application_immutable()");

            migrationBuilder.DropColumn(name: "CurrentPricingRevision", table: "UsageEvents");
        }
    }
}
