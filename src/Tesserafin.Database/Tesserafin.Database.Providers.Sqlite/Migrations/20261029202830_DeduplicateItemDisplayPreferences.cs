using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tesserafin.Server.Implementations.Migrations
{
    /// <summary>
    /// Reduces every <c>ItemDisplayPreferences</c> scope (user, item, client) to one row and makes the
    /// scope unique. The row with the lowest id, which the server already reads, stays active; every
    /// other row of the scope is first copied in full to <c>ItemDisplayPreferencesArchive</c>, then
    /// removed. All of it runs in the migration's transaction.
    /// </summary>
    /// <remarks>
    /// Every statement is written so that running the migration again on a database that already has
    /// its table and index archives and removes only the duplicates present then: a full-system restore
    /// of an earlier backup brings back a migration history without this migration.
    /// </remarks>
    public partial class DeduplicateItemDisplayPreferences : Migration
    {
        // A row is a duplicate when its scope has a row with a lower id.
        private const string IsDuplicate =
            """
            "p"."Id" > (SELECT MIN("q"."Id") FROM "ItemDisplayPreferences" AS "q"
                        WHERE "q"."UserId" = "p"."UserId" AND "q"."ItemId" = "p"."ItemId" AND "q"."Client" = "p"."Client")
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE TABLE IF NOT EXISTS "ItemDisplayPreferencesArchive" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_ItemDisplayPreferencesArchive" PRIMARY KEY,
                    "UserId" TEXT NOT NULL,
                    "ItemId" TEXT NOT NULL,
                    "Client" TEXT NOT NULL,
                    "ViewType" INTEGER NOT NULL,
                    "RememberIndexing" INTEGER NOT NULL,
                    "IndexBy" INTEGER NULL,
                    "RememberSorting" INTEGER NOT NULL,
                    "SortBy" TEXT NOT NULL,
                    "SortOrder" INTEGER NOT NULL,
                    "KeptId" INTEGER NOT NULL,
                    "DuplicateClass" INTEGER NOT NULL,
                    "ArchivedAt" TEXT NOT NULL
                );
                """);

            // DuplicateClass: 2 = global scope (Guid.Empty item), 0 = every row of the scope holds the
            // same settings, 1 = the rows of the scope disagree.
            migrationBuilder.Sql(
                $"""
                INSERT INTO "ItemDisplayPreferencesArchive"
                    ("Id", "UserId", "ItemId", "Client", "ViewType", "RememberIndexing", "IndexBy", "RememberSorting",
                     "SortBy", "SortOrder", "KeptId", "DuplicateClass", "ArchivedAt")
                SELECT "p"."Id", "p"."UserId", "p"."ItemId", "p"."Client", "p"."ViewType", "p"."RememberIndexing",
                       "p"."IndexBy", "p"."RememberSorting", "p"."SortBy", "p"."SortOrder",
                       (SELECT MIN("k"."Id") FROM "ItemDisplayPreferences" AS "k"
                        WHERE "k"."UserId" = "p"."UserId" AND "k"."ItemId" = "p"."ItemId" AND "k"."Client" = "p"."Client"),
                       CASE
                           WHEN "p"."ItemId" = '00000000-0000-0000-0000-000000000000' THEN 2
                           WHEN (SELECT COUNT(DISTINCT "s"."SortBy" || '|' || "s"."SortOrder" || '|' || "s"."ViewType"
                                         || '|' || IFNULL("s"."IndexBy", '-') || '|' || "s"."RememberSorting"
                                         || '|' || "s"."RememberIndexing")
                                 FROM "ItemDisplayPreferences" AS "s"
                                 WHERE "s"."UserId" = "p"."UserId" AND "s"."ItemId" = "p"."ItemId" AND "s"."Client" = "p"."Client") = 1 THEN 0
                           ELSE 1
                       END,
                       strftime('%Y-%m-%d %H:%M:%f', 'now')
                FROM "ItemDisplayPreferences" AS "p"
                WHERE {IsDuplicate};
                """);

            migrationBuilder.Sql(
                $"""
                DELETE FROM "ItemDisplayPreferences" AS "p"
                WHERE {IsDuplicate};
                """);

            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_ItemDisplayPreferences_UserId";""");
            migrationBuilder.Sql(
                """
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_ItemDisplayPreferences_UserId_ItemId_Client"
                    ON "ItemDisplayPreferences" ("UserId", "ItemId", "Client");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ItemDisplayPreferences_UserId_ItemId_Client",
                table: "ItemDisplayPreferences");

            // The archived rows go back with their original ids: the table is back to what it held
            // before the migration.
            migrationBuilder.Sql(
                """
                INSERT INTO "ItemDisplayPreferences"
                    ("Id", "UserId", "ItemId", "Client", "ViewType", "RememberIndexing", "IndexBy", "RememberSorting",
                     "SortBy", "SortOrder")
                SELECT "Id", "UserId", "ItemId", "Client", "ViewType", "RememberIndexing", "IndexBy", "RememberSorting",
                       "SortBy", "SortOrder"
                FROM "ItemDisplayPreferencesArchive";
                """);

            migrationBuilder.DropTable(
                name: "ItemDisplayPreferencesArchive");

            migrationBuilder.CreateIndex(
                name: "IX_ItemDisplayPreferences_UserId",
                table: "ItemDisplayPreferences",
                column: "UserId");
        }
    }
}
