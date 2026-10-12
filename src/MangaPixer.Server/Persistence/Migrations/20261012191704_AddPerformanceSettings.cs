using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace com.lifepixer.mangapixer.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPerformanceSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PerformanceBackgroundWhileReading",
                table: "app_settings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PerformanceMediaWorkers",
                table: "app_settings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PerformancePageCacheBytes",
                table: "app_settings",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PerformanceBackgroundWhileReading",
                table: "app_settings");

            migrationBuilder.DropColumn(
                name: "PerformanceMediaWorkers",
                table: "app_settings");

            migrationBuilder.DropColumn(
                name: "PerformancePageCacheBytes",
                table: "app_settings");
        }
    }
}
