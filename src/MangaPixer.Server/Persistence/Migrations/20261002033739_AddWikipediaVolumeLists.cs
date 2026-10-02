using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace com.lifepixer.mangapixer.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWikipediaVolumeLists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "wikipedia_lists",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RecordId = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Method = table.Column<int>(type: "INTEGER", nullable: false),
                    AdminTitle = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    PagesJson = table.Column<string>(type: "TEXT", nullable: true),
                    RejectCode = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    DetailsJson = table.Column<string>(type: "TEXT", nullable: true),
                    CheckedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    NextCheckAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wikipedia_lists", x => x.Id);
                    table.ForeignKey(
                        name: "FK_wikipedia_lists_metadata_records_RecordId",
                        column: x => x.RecordId,
                        principalTable: "metadata_records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_wikipedia_lists_RecordId",
                table: "wikipedia_lists",
                column: "RecordId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "wikipedia_lists");
        }
    }
}
