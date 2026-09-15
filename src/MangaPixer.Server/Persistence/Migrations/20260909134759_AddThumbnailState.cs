using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace com.lifepixer.mangapixer.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddThumbnailState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ThumbnailContentVersion",
                table: "archive_items",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ThumbnailState",
                table: "archive_items",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_archive_items_ThumbnailState",
                table: "archive_items",
                column: "ThumbnailState");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_archive_items_ThumbnailState",
                table: "archive_items");

            migrationBuilder.DropColumn(
                name: "ThumbnailContentVersion",
                table: "archive_items");

            migrationBuilder.DropColumn(
                name: "ThumbnailState",
                table: "archive_items");
        }
    }
}
