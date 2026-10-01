using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace com.lifepixer.mangapixer.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTrashSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "BundlesLastCleanedAt",
                table: "app_settings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "BundlesLastCleanedAutomatic",
                table: "app_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "BundlesLastCleanedBytes",
                table: "app_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "BundlesLastCleanedFiles",
                table: "app_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "TrashAutoCleanEnabled",
                table: "app_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "TrashAutoCleanEnabledAt",
                table: "app_settings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TrashLastAutoRunAt",
                table: "app_settings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TrashLastEmptiedAt",
                table: "app_settings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TrashLastEmptiedAutomatic",
                table: "app_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "TrashLastEmptiedBytes",
                table: "app_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "TrashLastEmptiedHeldLibraries",
                table: "app_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TrashLastEmptiedNodes",
                table: "app_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BundlesLastCleanedAt",
                table: "app_settings");

            migrationBuilder.DropColumn(
                name: "BundlesLastCleanedAutomatic",
                table: "app_settings");

            migrationBuilder.DropColumn(
                name: "BundlesLastCleanedBytes",
                table: "app_settings");

            migrationBuilder.DropColumn(
                name: "BundlesLastCleanedFiles",
                table: "app_settings");

            migrationBuilder.DropColumn(
                name: "TrashAutoCleanEnabled",
                table: "app_settings");

            migrationBuilder.DropColumn(
                name: "TrashAutoCleanEnabledAt",
                table: "app_settings");

            migrationBuilder.DropColumn(
                name: "TrashLastAutoRunAt",
                table: "app_settings");

            migrationBuilder.DropColumn(
                name: "TrashLastEmptiedAt",
                table: "app_settings");

            migrationBuilder.DropColumn(
                name: "TrashLastEmptiedAutomatic",
                table: "app_settings");

            migrationBuilder.DropColumn(
                name: "TrashLastEmptiedBytes",
                table: "app_settings");

            migrationBuilder.DropColumn(
                name: "TrashLastEmptiedHeldLibraries",
                table: "app_settings");

            migrationBuilder.DropColumn(
                name: "TrashLastEmptiedNodes",
                table: "app_settings");
        }
    }
}
