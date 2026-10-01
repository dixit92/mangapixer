using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace com.lifepixer.mangapixer.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMatcherRulesRevision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RulesRevision",
                table: "metadata_match_queue",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RulesRevision",
                table: "metadata_match_queue");
        }
    }
}
