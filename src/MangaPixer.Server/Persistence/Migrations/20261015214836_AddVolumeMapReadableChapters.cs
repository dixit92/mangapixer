using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace com.lifepixer.mangapixer.Server.Persistence.Migrations
{
    /// <summary>
    /// 1.43.0 (MangaList, owner 2026-10-15): <c>series_volume_maps.ReadableChaptersJson</c> - the released chapters MangaDex can actually
    /// be read on (nullable; filled at each list's next refresh), for the metadata export's <c>mangadexReleased.readable</c>.
    /// </summary>
    public partial class AddVolumeMapReadableChapters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReadableChaptersJson",
                table: "series_volume_maps",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReadableChaptersJson",
                table: "series_volume_maps");
        }
    }
}
