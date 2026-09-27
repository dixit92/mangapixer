using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace com.lifepixer.mangapixer.Server.Persistence.Migrations
{
    /// <summary>
    /// Per-user "Series information on hover" (1.27.0). Additive: one non-null column
    /// whose default is TRUE, so every existing preferences row reads as ON (the owner's
    /// default); new rows get true from the entity initializer.
    /// </summary>
    public partial class AddSeriesInfoOnHover : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SeriesInfoOnHover",
                table: "reader_preferences",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SeriesInfoOnHover",
                table: "reader_preferences");
        }
    }
}
