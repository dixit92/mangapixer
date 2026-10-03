using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace com.lifepixer.mangapixer.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExportState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "export_carries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    NewNodeId = table.Column<long>(type: "INTEGER", nullable: false),
                    OldNodeId = table.Column<long>(type: "INTEGER", nullable: false),
                    OldNodePublicId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    At = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_export_carries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "export_items",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LibraryId = table.Column<long>(type: "INTEGER", nullable: false),
                    NodeId = table.Column<long>(type: "INTEGER", nullable: false),
                    NodePublicId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_export_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_export_items_libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "export_library_states",
                columns: table => new
                {
                    LibraryId = table.Column<long>(type: "INTEGER", nullable: false),
                    WatermarkAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastRebuildAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastRebuildMs = table.Column<long>(type: "INTEGER", nullable: false),
                    ItemCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_export_library_states", x => x.LibraryId);
                    table.ForeignKey(
                        name: "FK_export_library_states_libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "export_removals",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LibraryId = table.Column<long>(type: "INTEGER", nullable: false),
                    NodePublicId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    At = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_export_removals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_export_removals_libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_export_carries_At",
                table: "export_carries",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_export_carries_NewNodeId",
                table: "export_carries",
                column: "NewNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_export_carries_OldNodeId",
                table: "export_carries",
                column: "OldNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_export_items_LibraryId_NodeId",
                table: "export_items",
                columns: new[] { "LibraryId", "NodeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_export_items_LibraryId_UpdatedAt_Id",
                table: "export_items",
                columns: new[] { "LibraryId", "UpdatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_export_removals_LibraryId_At",
                table: "export_removals",
                columns: new[] { "LibraryId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_export_removals_LibraryId_NodePublicId",
                table: "export_removals",
                columns: new[] { "LibraryId", "NodePublicId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "export_carries");

            migrationBuilder.DropTable(
                name: "export_items");

            migrationBuilder.DropTable(
                name: "export_library_states");

            migrationBuilder.DropTable(
                name: "export_removals");
        }
    }
}
