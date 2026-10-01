using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace com.lifepixer.mangapixer.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCrossLibraryMoves : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "node_moves",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FromNodeId = table.Column<long>(type: "INTEGER", nullable: false),
                    ToNodeId = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_node_moves", x => x.Id);
                    table.ForeignKey(
                        name: "FK_node_moves_catalog_nodes_FromNodeId",
                        column: x => x.FromNodeId,
                        principalTable: "catalog_nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_node_moves_catalog_nodes_ToNodeId",
                        column: x => x.ToNodeId,
                        principalTable: "catalog_nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "move_conflicts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MoveId = table.Column<long>(type: "INTEGER", nullable: false),
                    UserId = table.Column<long>(type: "INTEGER", nullable: true),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ResolvedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ResolvedByUserId = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_move_conflicts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_move_conflicts_node_moves_MoveId",
                        column: x => x.MoveId,
                        principalTable: "node_moves",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_move_conflicts_users_ResolvedByUserId",
                        column: x => x.ResolvedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_move_conflicts_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_move_conflicts_MoveId_UserId_Kind",
                table: "move_conflicts",
                columns: new[] { "MoveId", "UserId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_move_conflicts_ResolvedByUserId",
                table: "move_conflicts",
                column: "ResolvedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_move_conflicts_State_CreatedAt",
                table: "move_conflicts",
                columns: new[] { "State", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_move_conflicts_UserId",
                table: "move_conflicts",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_node_moves_FromNodeId",
                table: "node_moves",
                column: "FromNodeId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_node_moves_ToNodeId",
                table: "node_moves",
                column: "ToNodeId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "move_conflicts");

            migrationBuilder.DropTable(
                name: "node_moves");
        }
    }
}
