using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace com.lifepixer.mangapixer.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCommunityScores : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "CommunityScore",
                table: "metadata_records",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CommunityScoreAt",
                table: "metadata_records",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CommunityVotes",
                table: "metadata_records",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MalScore",
                table: "metadata_records",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MalScoreAt",
                table: "metadata_records",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MalVotes",
                table: "metadata_records",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CommunityScore",
                table: "metadata_records");

            migrationBuilder.DropColumn(
                name: "CommunityScoreAt",
                table: "metadata_records");

            migrationBuilder.DropColumn(
                name: "CommunityVotes",
                table: "metadata_records");

            migrationBuilder.DropColumn(
                name: "MalScore",
                table: "metadata_records");

            migrationBuilder.DropColumn(
                name: "MalScoreAt",
                table: "metadata_records");

            migrationBuilder.DropColumn(
                name: "MalVotes",
                table: "metadata_records");
        }
    }
}
