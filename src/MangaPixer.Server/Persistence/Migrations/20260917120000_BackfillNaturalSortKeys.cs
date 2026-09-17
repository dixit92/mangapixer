using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace com.lifepixer.mangapixer.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BackfillNaturalSortKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Natural name-sort correctness (1.15.0). No schema change: catalog_nodes.SortKey
            // already exists and is already indexed as (ParentId, Kind, SortKey). What changes
            // is its CONTENT.
            //
            // Until now the scanner stored the kind prefix followed by the RAW display name, so
            // ordinal ordering over that key put "Chapter 10" before "Chapter 2" on every surface
            // that reads it: browse, the keyset cursors, folder covers, next/prev, first-unread,
            // the continue row and the jump rail. SortKey.EncodeName - the encoder that produces a
            // key whose ordinal order IS natural order - existed but was only ever called from
            // tests. The scanner now stores SortKey.ForNode; this migration brings every EXISTING
            // row onto the same format so old and new nodes are comparable.
            //
            // Recomputed from the stored DisplayName via mp_sort_key, the SQLite user-defined
            // function bound to the very same C# encoder (SortKeySqlFunctionInterceptor). Doing it
            // this way - rather than re-implementing the character-level encoding as a recursive
            // CTE - means there is exactly one definition of the on-disk format, so the backfill
            // cannot drift away from what the scanner writes.
            //
            // Forward-only and set-based. The WHERE makes it a no-op for rows already in the new
            // format, so re-running it (or running it on a fresh, empty database) writes nothing.
            // Note the FTS5 sync trigger is AFTER UPDATE OF DisplayName, RelativePath, LibraryId,
            // so rewriting SortKey does not re-tokenise the search index.
            migrationBuilder.Sql("""
                UPDATE catalog_nodes
                SET SortKey = mp_sort_key(Kind, DisplayName)
                WHERE SortKey <> mp_sort_key(Kind, DisplayName);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restores the pre-1.15.0 raw key: the kind digit ("0" folder / "1" archive)
            // followed by the display name verbatim. Expressible in plain SQL, so a rollback
            // does not depend on the user-defined function being registered.
            migrationBuilder.Sql("""
                UPDATE catalog_nodes
                SET SortKey = CAST(Kind AS TEXT) || DisplayName;
                """);
        }
    }
}
