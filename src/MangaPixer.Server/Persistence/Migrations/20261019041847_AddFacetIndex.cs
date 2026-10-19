using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace com.lifepixer.mangapixer.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFacetIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalRef",
                table: "declared_facts",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "creators",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PublicId = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    DisplayNameSource = table.Column<int>(type: "INTEGER", nullable: false),
                    MergedIntoId = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_creators", x => x.Id);
                    table.ForeignKey(
                        name: "FK_creators_creators_MergedIntoId",
                        column: x => x.MergedIntoId,
                        principalTable: "creators",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "facet_index_libraries",
                columns: table => new
                {
                    LibraryId = table.Column<long>(type: "INTEGER", nullable: false),
                    InputsKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    BuiltAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Works = table.Column<int>(type: "INTEGER", nullable: false),
                    Entries = table.Column<int>(type: "INTEGER", nullable: false),
                    ElapsedMs = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_facet_index_libraries", x => x.LibraryId);
                    table.ForeignKey(
                        name: "FK_facet_index_libraries_libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "facet_works",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkKey = table.Column<string>(type: "TEXT", maxLength: 1515, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    RecordId = table.Column<long>(type: "INTEGER", nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    SortKey = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_facet_works", x => x.Id);
                    table.ForeignKey(
                        name: "FK_facet_works_metadata_records_RecordId",
                        column: x => x.RecordId,
                        principalTable: "metadata_records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "creator_names",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CreatorId = table.Column<long>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    NameKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Script = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_creator_names", x => x.Id);
                    table.ForeignKey(
                        name: "FK_creator_names_creators_CreatorId",
                        column: x => x.CreatorId,
                        principalTable: "creators",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "creator_overrides",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    TargetCreatorId = table.Column<long>(type: "INTEGER", nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ActorUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_creator_overrides", x => x.Id);
                    table.ForeignKey(
                        name: "FK_creator_overrides_creators_TargetCreatorId",
                        column: x => x.TargetCreatorId,
                        principalTable: "creators",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "creator_provider_ids",
                columns: table => new
                {
                    Provider = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatorId = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_creator_provider_ids", x => new { x.Provider, x.ExternalId });
                    table.ForeignKey(
                        name: "FK_creator_provider_ids_creators_CreatorId",
                        column: x => x.CreatorId,
                        principalTable: "creators",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "facet_values",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PublicId = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Bucket = table.Column<int>(type: "INTEGER", nullable: false),
                    ValueKey = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Display = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CreatorId = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_facet_values", x => x.Id);
                    table.ForeignKey(
                        name: "FK_facet_values_creators_CreatorId",
                        column: x => x.CreatorId,
                        principalTable: "creators",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "facet_entries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkId = table.Column<long>(type: "INTEGER", nullable: false),
                    LocationNodeId = table.Column<long>(type: "INTEGER", nullable: false),
                    LibraryId = table.Column<long>(type: "INTEGER", nullable: false),
                    ValueId = table.Column<long>(type: "INTEGER", nullable: false),
                    Sources = table.Column<int>(type: "INTEGER", nullable: false),
                    Roles = table.Column<int>(type: "INTEGER", nullable: false),
                    Conflict = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_facet_entries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_facet_entries_catalog_nodes_LocationNodeId",
                        column: x => x.LocationNodeId,
                        principalTable: "catalog_nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_facet_entries_facet_values_ValueId",
                        column: x => x.ValueId,
                        principalTable: "facet_values",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_facet_entries_facet_works_WorkId",
                        column: x => x.WorkId,
                        principalTable: "facet_works",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_facet_entries_libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_creator_names_CreatorId_Name",
                table: "creator_names",
                columns: new[] { "CreatorId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_creator_names_NameKey",
                table: "creator_names",
                column: "NameKey");

            migrationBuilder.CreateIndex(
                name: "IX_creator_overrides_Kind_Subject",
                table: "creator_overrides",
                columns: new[] { "Kind", "Subject" });

            migrationBuilder.CreateIndex(
                name: "IX_creator_overrides_TargetCreatorId",
                table: "creator_overrides",
                column: "TargetCreatorId");

            migrationBuilder.CreateIndex(
                name: "IX_creator_provider_ids_CreatorId",
                table: "creator_provider_ids",
                column: "CreatorId");

            migrationBuilder.CreateIndex(
                name: "IX_creators_MergedIntoId",
                table: "creators",
                column: "MergedIntoId");

            migrationBuilder.CreateIndex(
                name: "IX_creators_PublicId",
                table: "creators",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_facet_entries_LibraryId",
                table: "facet_entries",
                column: "LibraryId");

            migrationBuilder.CreateIndex(
                name: "IX_facet_entries_LocationNodeId",
                table: "facet_entries",
                column: "LocationNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_facet_entries_ValueId_LibraryId_WorkId",
                table: "facet_entries",
                columns: new[] { "ValueId", "LibraryId", "WorkId" });

            migrationBuilder.CreateIndex(
                name: "IX_facet_entries_ValueId_LocationNodeId",
                table: "facet_entries",
                columns: new[] { "ValueId", "LocationNodeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_facet_entries_WorkId",
                table: "facet_entries",
                column: "WorkId");

            migrationBuilder.CreateIndex(
                name: "IX_facet_values_Bucket_ValueKey",
                table: "facet_values",
                columns: new[] { "Bucket", "ValueKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_facet_values_CreatorId",
                table: "facet_values",
                column: "CreatorId");

            migrationBuilder.CreateIndex(
                name: "IX_facet_values_PublicId",
                table: "facet_values",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_facet_works_RecordId",
                table: "facet_works",
                column: "RecordId");

            migrationBuilder.CreateIndex(
                name: "IX_facet_works_WorkKey",
                table: "facet_works",
                column: "WorkKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "creator_names");

            migrationBuilder.DropTable(
                name: "creator_overrides");

            migrationBuilder.DropTable(
                name: "creator_provider_ids");

            migrationBuilder.DropTable(
                name: "facet_entries");

            migrationBuilder.DropTable(
                name: "facet_index_libraries");

            migrationBuilder.DropTable(
                name: "facet_values");

            migrationBuilder.DropTable(
                name: "facet_works");

            migrationBuilder.DropTable(
                name: "creators");

            migrationBuilder.DropColumn(
                name: "ExternalRef",
                table: "declared_facts");
        }
    }
}
