using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace com.lifepixer.mangapixer.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBackupSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "BackupIntervalHours",
                table: "app_settings",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BackupLocation",
                table: "app_settings",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BackupLocationMarkerId",
                table: "app_settings",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BackupRetentionCount",
                table: "app_settings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "BackupsEnabled",
                table: "app_settings",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BackupIntervalHours",
                table: "app_settings");

            migrationBuilder.DropColumn(
                name: "BackupLocation",
                table: "app_settings");

            migrationBuilder.DropColumn(
                name: "BackupLocationMarkerId",
                table: "app_settings");

            migrationBuilder.DropColumn(
                name: "BackupRetentionCount",
                table: "app_settings");

            migrationBuilder.DropColumn(
                name: "BackupsEnabled",
                table: "app_settings");
        }
    }
}
