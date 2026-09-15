using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace com.lifepixer.mangapixer.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDefaultReaderMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DefaultReaderMode",
                table: "libraries",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "folder_reader_defaults",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    NodeId = table.Column<long>(type: "INTEGER", nullable: false),
                    ReaderMode = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_folder_reader_defaults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_folder_reader_defaults_catalog_nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "catalog_nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_folder_reader_defaults_NodeId",
                table: "folder_reader_defaults",
                column: "NodeId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "folder_reader_defaults");

            migrationBuilder.DropColumn(
                name: "DefaultReaderMode",
                table: "libraries");
        }
    }
}
