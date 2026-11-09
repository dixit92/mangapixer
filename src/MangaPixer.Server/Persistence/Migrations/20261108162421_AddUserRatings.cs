using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace com.lifepixer.mangapixer.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUserRatings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user_ratings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    CatalogNodeId = table.Column<long>(type: "INTEGER", nullable: false),
                    Score = table.Column<int>(type: "INTEGER", nullable: false),
                    RatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_ratings", x => x.Id);
                    table.CheckConstraint("CK_user_ratings_Score", "\"Score\" >= 1 AND \"Score\" <= 10");
                    table.ForeignKey(
                        name: "FK_user_ratings_catalog_nodes_CatalogNodeId",
                        column: x => x.CatalogNodeId,
                        principalTable: "catalog_nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_user_ratings_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_user_ratings_CatalogNodeId",
                table: "user_ratings",
                column: "CatalogNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_user_ratings_UserId_CatalogNodeId",
                table: "user_ratings",
                columns: new[] { "UserId", "CatalogNodeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_ratings_UserId_Score",
                table: "user_ratings",
                columns: new[] { "UserId", "Score" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_ratings");
        }
    }
}
