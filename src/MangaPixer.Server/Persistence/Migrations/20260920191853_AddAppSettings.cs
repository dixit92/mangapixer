using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace com.lifepixer.mangapixer.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAppSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "app_settings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdateCheckEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdateLastCheckedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdateLastKnownLatestVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_settings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_settings");
        }
    }
}
