using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class EnforceSchemaGuardConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every AddCheckConstraint below emits a plain ADD CONSTRAINT, which Postgres
            // validates against every existing row. Rows written before the matching C#
            // guards existed are unaudited, so a single violating row would abort the
            // whole migration with a bare check-violation and no row list. Refuse up front
            // with an actionable message instead, in the same style as the rollback guards.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "Subscriptions" WHERE NOT ("BillingDay" BETWEEN 1 AND 31)) THEN
                        RAISE EXCEPTION 'EnforceSchemaGuardConstraints refused: % Subscriptions rows violate CK_Subscription_BillingDay_Valid ("BillingDay" BETWEEN 1 AND 31). First violating ids: %. Reconcile or remove them, then re-run the migration.',
                            (SELECT count(*) FROM "Subscriptions" WHERE NOT ("BillingDay" BETWEEN 1 AND 31)),
                            (SELECT string_agg("Id"::text, ', ') FROM (SELECT "Id" FROM "Subscriptions" WHERE NOT ("BillingDay" BETWEEN 1 AND 31) LIMIT 10) violating);
                    END IF;
                END $$;
                """
            );

            migrationBuilder.AddCheckConstraint(
                name: "CK_Subscription_BillingDay_Valid",
                table: "Subscriptions",
                sql: "\"BillingDay\" BETWEEN 1 AND 31"
            );

            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "SpendEntries" WHERE NOT ("Currency" ~ '^[A-Z]{3}$')) THEN
                        RAISE EXCEPTION 'EnforceSchemaGuardConstraints refused: % SpendEntries rows violate CK_SpendEntry_Currency_Normalized ("Currency" ~ ''^[A-Z]{3}$''). First violating ids: %. Reconcile or remove them, then re-run the migration.',
                            (SELECT count(*) FROM "SpendEntries" WHERE NOT ("Currency" ~ '^[A-Z]{3}$')),
                            (SELECT string_agg("Id"::text, ', ') FROM (SELECT "Id" FROM "SpendEntries" WHERE NOT ("Currency" ~ '^[A-Z]{3}$') LIMIT 10) violating);
                    END IF;
                END $$;
                """
            );

            migrationBuilder.AddCheckConstraint(
                name: "CK_SpendEntry_Currency_Normalized",
                table: "SpendEntries",
                sql: "\"Currency\" ~ '^[A-Z]{3}$'"
            );

            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "NotificationSettings" WHERE NOT ("Id" = '33333333-3333-3333-3333-333333333301')) THEN
                        RAISE EXCEPTION 'EnforceSchemaGuardConstraints refused: % NotificationSettings rows violate CK_NotificationSettings_Singleton ("Id" = the singleton id). First violating ids: %. Reconcile or remove them, then re-run the migration.',
                            (SELECT count(*) FROM "NotificationSettings" WHERE NOT ("Id" = '33333333-3333-3333-3333-333333333301')),
                            (SELECT string_agg("Id"::text, ', ') FROM (SELECT "Id" FROM "NotificationSettings" WHERE NOT ("Id" = '33333333-3333-3333-3333-333333333301') LIMIT 10) violating);
                    END IF;
                END $$;
                """
            );

            migrationBuilder.AddCheckConstraint(
                name: "CK_NotificationSettings_Singleton",
                table: "NotificationSettings",
                sql: "\"Id\" = '33333333-3333-3333-3333-333333333301'"
            );

            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "DailyAggregates" WHERE NOT ("UnknownCacheSavingsCount" >= 0)) THEN
                        RAISE EXCEPTION 'EnforceSchemaGuardConstraints refused: % DailyAggregates rows violate CK_DailyAggregate_UnknownCacheSavingsCount_NonNegative ("UnknownCacheSavingsCount" >= 0). First violating (Date, Provider, Model) keys: %. Reconcile or remove them, then re-run the migration.',
                            (SELECT count(*) FROM "DailyAggregates" WHERE NOT ("UnknownCacheSavingsCount" >= 0)),
                            (SELECT string_agg("Date"::text || ' / ' || "Provider" || ' / ' || "Model", ', ') FROM (SELECT "Date", "Provider", "Model" FROM "DailyAggregates" WHERE NOT ("UnknownCacheSavingsCount" >= 0) LIMIT 10) violating);
                    END IF;
                END $$;
                """
            );

            migrationBuilder.AddCheckConstraint(
                name: "CK_DailyAggregate_UnknownCacheSavingsCount_NonNegative",
                table: "DailyAggregates",
                sql: "\"UnknownCacheSavingsCount\" >= 0"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(name: "CK_Subscription_BillingDay_Valid", table: "Subscriptions");

            migrationBuilder.DropCheckConstraint(name: "CK_SpendEntry_Currency_Normalized", table: "SpendEntries");

            migrationBuilder.DropCheckConstraint(
                name: "CK_NotificationSettings_Singleton",
                table: "NotificationSettings"
            );

            migrationBuilder.DropCheckConstraint(
                name: "CK_DailyAggregate_UnknownCacheSavingsCount_NonNegative",
                table: "DailyAggregates"
            );
        }
    }
}
