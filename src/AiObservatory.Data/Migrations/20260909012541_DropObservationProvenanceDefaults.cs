using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropObservationProvenanceDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // These DROP DEFAULTs were first appended to AddObservationProvenance after that
            // migration was already recorded in production, so they only ever ran on fresh
            // databases and the deployed schema kept every default. They live here now so
            // every database runs them exactly once. DROP DEFAULT is a no-op when no default
            // exists, so a database created while the block still sat in
            // AddObservationProvenance (and so already dropped them) tolerates this replay.
            // The why is unchanged: a later direct insert that omits provenance must fail,
            // not silently acquire synthetic 'legacy-api' / 'Legacy' / epoch values that are
            // indistinguishable from genuine legacy data.
            migrationBuilder.Sql(
                """
                ALTER TABLE "UsageEvents" ALTER COLUMN "SourceId" DROP DEFAULT;
                ALTER TABLE "UsageEvents" ALTER COLUMN "SourceKind" DROP DEFAULT;
                ALTER TABLE "UsageEvents" ALTER COLUMN "UsageScope" DROP DEFAULT;
                ALTER TABLE "UsageEvents" ALTER COLUMN "CostBasis" DROP DEFAULT;
                ALTER TABLE "UsageEvents" ALTER COLUMN "ObservedAt" DROP DEFAULT;
                ALTER TABLE "SpendEntries" ALTER COLUMN "SourceId" DROP DEFAULT;
                ALTER TABLE "SpendEntries" ALTER COLUMN "SourceKind" DROP DEFAULT;
                ALTER TABLE "SpendEntries" ALTER COLUMN "UsageScope" DROP DEFAULT;
                ALTER TABLE "SpendEntries" ALTER COLUMN "CostBasis" DROP DEFAULT;
                ALTER TABLE "SpendEntries" ALTER COLUMN "ObservedAt" DROP DEFAULT;
                ALTER TABLE "DailyAggregates" ALTER COLUMN "SourceId" DROP DEFAULT;
                ALTER TABLE "DailyAggregates" ALTER COLUMN "SourceKind" DROP DEFAULT;
                ALTER TABLE "DailyAggregates" ALTER COLUMN "UsageScope" DROP DEFAULT;
                ALTER TABLE "DailyAggregates" ALTER COLUMN "CostBasis" DROP DEFAULT;
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restore exactly the synthetic defaults AddObservationProvenance added, so a
            // rollback returns the schema to that migration's post-apply state.
            migrationBuilder.Sql(
                """
                ALTER TABLE "UsageEvents" ALTER COLUMN "SourceId" SET DEFAULT 'legacy-api';
                ALTER TABLE "UsageEvents" ALTER COLUMN "SourceKind" SET DEFAULT 'Legacy';
                ALTER TABLE "UsageEvents" ALTER COLUMN "UsageScope" SET DEFAULT 'Unknown';
                ALTER TABLE "UsageEvents" ALTER COLUMN "CostBasis" SET DEFAULT 'Unknown';
                ALTER TABLE "UsageEvents" ALTER COLUMN "ObservedAt" SET DEFAULT '1970-01-01T00:00:00Z'::timestamp with time zone;
                ALTER TABLE "SpendEntries" ALTER COLUMN "SourceId" SET DEFAULT 'legacy-spend';
                ALTER TABLE "SpendEntries" ALTER COLUMN "SourceKind" SET DEFAULT 'Legacy';
                ALTER TABLE "SpendEntries" ALTER COLUMN "UsageScope" SET DEFAULT 'Unknown';
                ALTER TABLE "SpendEntries" ALTER COLUMN "CostBasis" SET DEFAULT 'Billed';
                ALTER TABLE "SpendEntries" ALTER COLUMN "ObservedAt" SET DEFAULT '1970-01-01T00:00:00Z'::timestamp with time zone;
                ALTER TABLE "DailyAggregates" ALTER COLUMN "SourceId" SET DEFAULT 'legacy-api';
                ALTER TABLE "DailyAggregates" ALTER COLUMN "SourceKind" SET DEFAULT 'Legacy';
                ALTER TABLE "DailyAggregates" ALTER COLUMN "UsageScope" SET DEFAULT 'Unknown';
                ALTER TABLE "DailyAggregates" ALTER COLUMN "CostBasis" SET DEFAULT 'Unknown';
                """
            );
        }
    }
}
