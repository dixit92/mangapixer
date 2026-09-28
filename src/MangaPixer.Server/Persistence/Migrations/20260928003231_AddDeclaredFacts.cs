using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace com.lifepixer.mangapixer.Server.Persistence.Migrations
{
    /// <summary>
    /// The 1.28.0 cycle's one migration, additive only: the generic <c>declared_facts</c>
    /// table (admin-declared type / creators on a folder or a library; cascade-deleted
    /// with its node or library) and two <c>app_settings</c> columns for other lanes -
    /// <c>MetadataCoverCompareEnabled</c> (NOT NULL, default TRUE so the existing row reads
    /// "Compare covers" as on) and <c>MetadataProvidersJson</c> (null = default providers).
    /// </summary>
    public partial class AddDeclaredFacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "MetadataCoverCompareEnabled",
                table: "app_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "MetadataProvidersJson",
                table: "app_settings",
                type: "TEXT",
                maxLength: 4096,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "declared_facts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LibraryId = table.Column<long>(type: "INTEGER", nullable: false),
                    NodeId = table.Column<long>(type: "INTEGER", nullable: true),
                    Key = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Role = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_declared_facts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_declared_facts_catalog_nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "catalog_nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_declared_facts_libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_declared_facts_LibraryId_Key",
                table: "declared_facts",
                columns: new[] { "LibraryId", "Key" });

            migrationBuilder.CreateIndex(
                name: "IX_declared_facts_NodeId_Key",
                table: "declared_facts",
                columns: new[] { "NodeId", "Key" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "declared_facts");

            migrationBuilder.DropColumn(
                name: "MetadataCoverCompareEnabled",
                table: "app_settings");

            migrationBuilder.DropColumn(
                name: "MetadataProvidersJson",
                table: "app_settings");
        }
    }
}
