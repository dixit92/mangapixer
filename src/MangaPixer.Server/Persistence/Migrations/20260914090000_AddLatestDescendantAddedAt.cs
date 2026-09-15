using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace com.lifepixer.mangapixer.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLatestDescendantAddedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Maintained per-folder recency primitive (1.12.0). Nullable: a folder with
            // no non-tombstoned descendant archive stays null, and an archive row is
            // always null (consumers use its own CreatedAt). Stored as INTEGER because
            // every DateTimeOffset is persisted through DateTimeOffsetToBinaryConverter.
            migrationBuilder.AddColumn<long>(
                name: "LatestDescendantAddedAt",
                table: "catalog_nodes",
                type: "INTEGER",
                nullable: true);

            // One-time backfill for existing folders: set each folder's value to the MAX
            // CreatedAt over its non-tombstoned descendant ARCHIVES at any depth (recursive
            // descendant CTE, modeled on CatalogBrowseService.ResolveFolderCoversAsync /
            // ResolveFolderReadRollupsAsync). Folders with no such descendant archive are
            // left null. CreatedAt is the order-preserving binary long, so MAX() over it is
            // the chronologically-latest instant and copies verbatim into the new column.
            migrationBuilder.Sql("""
                WITH RECURSIVE descendants(RootId, NodeId) AS (
                    SELECT Id, Id FROM catalog_nodes WHERE Kind = 0
                    UNION ALL
                    SELECT d.RootId, cn.Id FROM descendants d
                    JOIN catalog_nodes cn ON cn.ParentId = d.NodeId
                ),
                maxes AS (
                    SELECT d.RootId AS RootId, MAX(a.CreatedAt) AS MaxCreated
                    FROM descendants d
                    JOIN catalog_nodes a ON a.Id = d.NodeId AND a.Kind = 1 AND a.Availability != 5
                    GROUP BY d.RootId
                )
                UPDATE catalog_nodes
                SET LatestDescendantAddedAt = (SELECT MaxCreated FROM maxes WHERE maxes.RootId = catalog_nodes.Id)
                WHERE Kind = 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LatestDescendantAddedAt",
                table: "catalog_nodes");
        }
    }
}
