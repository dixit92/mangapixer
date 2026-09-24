using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace com.lifepixer.mangapixer.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddArchiveSpreadLayout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "archive_spread_layouts",
                columns: table => new
                {
                    NodeId = table.Column<long>(type: "INTEGER", nullable: false),
                    ContentVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    SpreadStartsJson = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_archive_spread_layouts", x => x.NodeId);
                    table.ForeignKey(
                        name: "FK_archive_spread_layouts_catalog_nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "catalog_nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "archive_spread_layouts");
        }
    }
}
