using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemovePairedLegacyGitHubSpend : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The delete is intentional (the rows are stale duplicates of retained
            // billing-observation entries) and Down stays a no-op — reinserting them
            // would restore the double-count. But a financial-row deletion on deploy
            // must leave a trail, so the affected row count is raised as a NOTICE.
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    deleted_count integer;
                BEGIN
                    WITH deleted AS (
                        DELETE FROM "SpendEntries" AS legacy
                        WHERE legacy."Source" = 'Api'
                          AND legacy."SourceId" = 'legacy-spend'
                          AND legacy."EntryKey" LIKE 'github:%'
                          AND EXISTS (
                              SELECT 1
                              FROM "SpendEntries" AS canonical
                              WHERE canonical."Source" = 'Api'
                                AND canonical."SourceId" = 'github-billing-api'
                                AND canonical."EntryKey" = 'billing:github-billing-api:' || legacy."EntryKey"
                          )
                        RETURNING 1
                    )
                    SELECT count(*) INTO deleted_count FROM deleted;
                    RAISE NOTICE 'RemovePairedLegacyGitHubSpend deleted % paired legacy GitHub spend rows', deleted_count;
                END $$;
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Removed rows are stale snapshots with canonical retained-observation
            // counterparts. Recreating them would restore the financial double-count.
        }
    }
}
