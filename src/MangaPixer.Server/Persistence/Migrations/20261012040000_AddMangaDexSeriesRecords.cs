using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace com.lifepixer.mangapixer.Server.Persistence.Migrations
{
    /// <summary>
    /// 1.42.0 (lane D, MangaDex as the series record): <c>app_settings.MetadataMangaDexStepEnabled</c> - "Search MangaDex when
    /// MangaUpdates finds nothing" under Automatic matching (NOT NULL, default TRUE so the existing row reads on, as the owner chose).
    /// </summary>
    public partial class AddMangaDexSeriesRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "MetadataMangaDexStepEnabled",
                table: "app_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MetadataMangaDexStepEnabled",
                table: "app_settings");
        }
    }
}
