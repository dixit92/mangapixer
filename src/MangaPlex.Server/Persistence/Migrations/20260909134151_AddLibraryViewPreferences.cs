using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace com.lifepixer.mangaplex.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLibraryViewPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LibraryGridDensity",
                table: "reader_preferences",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "LibrarySort",
                table: "reader_preferences",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "LibraryViewMode",
                table: "reader_preferences",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LibraryGridDensity",
                table: "reader_preferences");

            migrationBuilder.DropColumn(
                name: "LibrarySort",
                table: "reader_preferences");

            migrationBuilder.DropColumn(
                name: "LibraryViewMode",
                table: "reader_preferences");
        }
    }
}
