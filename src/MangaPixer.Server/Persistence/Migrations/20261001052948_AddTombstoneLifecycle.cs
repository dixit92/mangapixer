using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace com.lifepixer.mangapixer.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTombstoneLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "TombstonedAt",
                table: "catalog_nodes",
                type: "INTEGER",
                nullable: true);

            // Existing tombstones: the scan that tombstoned a node set UpdatedAt then (a tombstoned node is not updated again), so it
            // is the tombstone time. Both columns use the same DateTimeOffset-to-binary encoding, so the value copies as is.
            migrationBuilder.Sql("UPDATE catalog_nodes SET TombstonedAt = COALESCE(UpdatedAt, CreatedAt) WHERE Availability = 5;");

            migrationBuilder.AddColumn<int>(
                name: "TrashRetentionDays",
                table: "app_settings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_catalog_nodes_Availability_TombstonedAt",
                table: "catalog_nodes",
                columns: new[] { "Availability", "TombstonedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_archive_items_ByteLength_ContentSignature",
                table: "archive_items",
                columns: new[] { "ByteLength", "ContentSignature" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_catalog_nodes_Availability_TombstonedAt",
                table: "catalog_nodes");

            migrationBuilder.DropIndex(
                name: "IX_archive_items_ByteLength_ContentSignature",
                table: "archive_items");

            migrationBuilder.DropColumn(
                name: "TombstonedAt",
                table: "catalog_nodes");

            migrationBuilder.DropColumn(
                name: "TrashRetentionDays",
                table: "app_settings");
        }
    }
}
