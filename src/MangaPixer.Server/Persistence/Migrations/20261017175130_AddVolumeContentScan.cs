using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace com.lifepixer.mangapixer.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVolumeContentScan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "archive_page_hashes",
                columns: table => new
                {
                    NodeId = table.Column<long>(type: "INTEGER", nullable: false),
                    ContentVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    PageCount = table.Column<int>(type: "INTEGER", nullable: false),
                    HashedPages = table.Column<int>(type: "INTEGER", nullable: false),
                    Hashes = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Unhashed = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Algorithm = table.Column<int>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    HashedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_archive_page_hashes", x => x.NodeId);
                    table.ForeignKey(
                        name: "FK_archive_page_hashes_archive_items_NodeId",
                        column: x => x.NodeId,
                        principalTable: "archive_items",
                        principalColumn: "NodeId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "volume_contents",
                columns: table => new
                {
                    VolumeNodeId = table.Column<long>(type: "INTEGER", nullable: false),
                    Volume = table.Column<double>(type: "REAL", nullable: false),
                    VolumeContentVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    FolderId = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    ChaptersJson = table.Column<string>(type: "TEXT", nullable: false),
                    NearMisses = table.Column<int>(type: "INTEGER", nullable: false),
                    CoveredPages = table.Column<int>(type: "INTEGER", nullable: false),
                    CandidatesKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ScannedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_volume_contents", x => x.VolumeNodeId);
                    table.ForeignKey(
                        name: "FK_volume_contents_archive_items_VolumeNodeId",
                        column: x => x.VolumeNodeId,
                        principalTable: "archive_items",
                        principalColumn: "NodeId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_volume_contents_catalog_nodes_FolderId",
                        column: x => x.FolderId,
                        principalTable: "catalog_nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "volume_scan_state",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_volume_scan_state", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_volume_contents_FolderId",
                table: "volume_contents",
                column: "FolderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "archive_page_hashes");

            migrationBuilder.DropTable(
                name: "volume_contents");

            migrationBuilder.DropTable(
                name: "volume_scan_state");
        }
    }
}
