using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace com.lifepixer.mangapixer.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CaseInsensitiveSortKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Case-insensitive name sort. No schema change: catalog_nodes.SortKey and its
            // (ParentId, Kind, SortKey) index stay as they are; what changes is the CONTENT.
            //
            // The key used to copy letters verbatim, so ordinal order put every capitalised
            // name before every lower-case one ("Zebra" before "apple") on every surface that
            // reads it: browse, the keyset cursors, folder covers, next/prev, first-unread and
            // the jump rail, whose lower-case names formed a second run after 'Z'. SortKey.ForNode
            // now stores the case-folded name followed by the name as spelled (a tie-breaker, so
            // siblings differing only in case keep distinct keys); this brings every EXISTING row
            // onto that format so old and new nodes are comparable.
            //
            // Same shape as BackfillNaturalSortKeys: recomputed from DisplayName through
            // mp_sort_key, the SQLite function bound to the C# encoder itself, so there is one
            // definition of the on-disk format. The WHERE makes it a no-op for rows already in the
            // new format (and on an empty database). The FTS5 sync trigger fires only on
            // DisplayName / RelativePath / LibraryId, so the search index is not re-tokenised.
            migrationBuilder.Sql("""
                UPDATE catalog_nodes
                SET SortKey = mp_sort_key(Kind, DisplayName)
                WHERE SortKey <> mp_sort_key(Kind, DisplayName);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restores the case-sensitive natural key: the kind digit followed by the part after
            // the U+0001 separator, which is exactly the previous encoding of the name as spelled.
            // Plain SQL, so a rollback does not depend on the user-defined function.
            migrationBuilder.Sql("""
                UPDATE catalog_nodes
                SET SortKey = substr(SortKey, 1, 1) || substr(SortKey, instr(SortKey, char(1)) + 1)
                WHERE instr(SortKey, char(1)) > 0;
                """);
        }
    }
}
