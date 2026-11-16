using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace com.lifepixer.mangapixer.Server.Persistence.Migrations
{
    /// <summary>
    /// 1.54.0 (migration #62), additive: <c>reader_preferences.HomeRowOrder</c>, nullable text - the user's order of the Home rows
    /// (normalized ids joined by commas, see <c>HomeRows</c>). Null for every existing row = the default order, so nothing changes for
    /// anyone until they reorder Home in Settings.
    /// </summary>
    public partial class AddHomeRowOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HomeRowOrder",
                table: "reader_preferences",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HomeRowOrder",
                table: "reader_preferences");
        }
    }
}
