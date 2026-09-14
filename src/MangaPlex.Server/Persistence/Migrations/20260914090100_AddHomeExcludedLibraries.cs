using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace com.lifepixer.mangaplex.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHomeExcludedLibraries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "home_excluded_libraries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    LibraryId = table.Column<long>(type: "INTEGER", nullable: false),
                    MarkedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_home_excluded_libraries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_home_excluded_libraries_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_home_excluded_libraries_UserId",
                table: "home_excluded_libraries",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_home_excluded_libraries_UserId_LibraryId",
                table: "home_excluded_libraries",
                columns: new[] { "UserId", "LibraryId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "home_excluded_libraries");
        }
    }
}
