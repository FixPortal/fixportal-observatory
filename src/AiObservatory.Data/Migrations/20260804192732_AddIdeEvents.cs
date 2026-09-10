using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIdeEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IdeEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PartnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(
                        type: "character varying(256)",
                        maxLength: 256,
                        nullable: false
                    ),
                    EventType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    EnvelopeJson = table.Column<string>(type: "jsonb", nullable: false),
                    ContentSha256 = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: false),
                    OccurredAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ReceivedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdeEvents", x => x.Id);
                }
            );

            migrationBuilder.CreateIndex(name: "IX_IdeEvents_OccurredAt", table: "IdeEvents", column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_IdeEvents_PartnerId_IdempotencyKey",
                table: "IdeEvents",
                columns: new[] { "PartnerId", "IdempotencyKey" },
                unique: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "IdeEvents");
        }
    }
}
