using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace com.lifepixer.mangapixer.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFavorites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "FavoritesSearchProminence",
                table: "reader_preferences",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ShowFavoritesHomeRow",
                table: "reader_preferences",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "favorites",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    CatalogNodeId = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_favorites", x => x.Id);
                    table.ForeignKey(
                        name: "FK_favorites_catalog_nodes_CatalogNodeId",
                        column: x => x.CatalogNodeId,
                        principalTable: "catalog_nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_favorites_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_favorites_CatalogNodeId",
                table: "favorites",
                column: "CatalogNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_favorites_UserId_CatalogNodeId",
                table: "favorites",
                columns: new[] { "UserId", "CatalogNodeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_favorites_UserId_CreatedAt",
                table: "favorites",
                columns: new[] { "UserId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "favorites");

            migrationBuilder.DropColumn(
                name: "FavoritesSearchProminence",
                table: "reader_preferences");

            migrationBuilder.DropColumn(
                name: "ShowFavoritesHomeRow",
                table: "reader_preferences");
        }
    }
}
