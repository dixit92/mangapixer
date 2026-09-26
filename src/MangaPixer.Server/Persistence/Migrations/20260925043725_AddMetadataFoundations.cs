using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace com.lifepixer.mangapixer.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMetadataFoundations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "MetadataEnabled",
                table: "libraries",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "MetadataPrecedence",
                table: "libraries",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MetadataSeriesInfoHidden",
                table: "libraries",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "MetadataBackoffStep",
                table: "app_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "MetadataBackoffUntil",
                table: "app_settings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MetadataBudgetDayUtc",
                table: "app_settings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MetadataBudgetUsed",
                table: "app_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "MetadataConsentAt",
                table: "app_settings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MetadataConsentVersion",
                table: "app_settings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MetadataDailyBudget",
                table: "app_settings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MetadataEnabled",
                table: "app_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "MetadataLastErrorAt",
                table: "app_settings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MetadataLastErrorCode",
                table: "app_settings",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MetadataSeriesInfoHidden",
                table: "app_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "embedded_metadata",
                columns: table => new
                {
                    NodeId = table.Column<long>(type: "INTEGER", nullable: false),
                    Schema = table.Column<int>(type: "INTEGER", nullable: false),
                    ContentVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Series = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    AlternateSeries = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    SeriesGroup = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    StoryArc = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Number = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Format = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    AgeRating = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    LanguageIso = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Volume = table.Column<int>(type: "INTEGER", nullable: true),
                    Count = table.Column<int>(type: "INTEGER", nullable: true),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    Month = table.Column<int>(type: "INTEGER", nullable: true),
                    Summary = table.Column<string>(type: "TEXT", nullable: true),
                    CreatorsJson = table.Column<string>(type: "TEXT", nullable: true),
                    Publisher = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Imprint = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    GenresJson = table.Column<string>(type: "TEXT", nullable: true),
                    TagsJson = table.Column<string>(type: "TEXT", nullable: true),
                    WebUrlsJson = table.Column<string>(type: "TEXT", nullable: true),
                    MangaDirection = table.Column<int>(type: "INTEGER", nullable: true),
                    Gtin = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    ReadAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_embedded_metadata", x => x.NodeId);
                    table.ForeignKey(
                        name: "FK_embedded_metadata_catalog_nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "catalog_nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "folder_metadata_precedence",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    NodeId = table.Column<long>(type: "INTEGER", nullable: false),
                    Precedence = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_folder_metadata_precedence", x => x.Id);
                    table.ForeignKey(
                        name: "FK_folder_metadata_precedence_catalog_nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "catalog_nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "metadata_records",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PublicId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SourceKind = table.Column<int>(type: "INTEGER", nullable: false),
                    RecordKind = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    AltTitlesJson = table.Column<string>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Origin = table.Column<int>(type: "INTEGER", nullable: true),
                    Format = table.Column<int>(type: "INTEGER", nullable: true),
                    Webtoon = table.Column<bool>(type: "INTEGER", nullable: true),
                    ProviderType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    StartYear = table.Column<int>(type: "INTEGER", nullable: true),
                    OriginStatus = table.Column<int>(type: "INTEGER", nullable: true),
                    OriginVolumes = table.Column<int>(type: "INTEGER", nullable: true),
                    LatestChapter = table.Column<double>(type: "REAL", nullable: true),
                    StatusText = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    LicensedEn = table.Column<bool>(type: "INTEGER", nullable: true),
                    TranslationComplete = table.Column<bool>(type: "INTEGER", nullable: true),
                    CreatorsJson = table.Column<string>(type: "TEXT", nullable: true),
                    GenresJson = table.Column<string>(type: "TEXT", nullable: true),
                    CategoriesJson = table.Column<string>(type: "TEXT", nullable: true),
                    PublishersJson = table.Column<string>(type: "TEXT", nullable: true),
                    CrossIdsJson = table.Column<string>(type: "TEXT", nullable: true),
                    SiteUrl = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    ImageRemoteUrl = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    ImageState = table.Column<int>(type: "INTEGER", nullable: false),
                    ImageVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    ProviderUpdatedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    FetchedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    FetchState = table.Column<int>(type: "INTEGER", nullable: false),
                    ExtraJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_metadata_records", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "node_series_links",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    NodeId = table.Column<long>(type: "INTEGER", nullable: false),
                    LibraryId = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    RecordId = table.Column<long>(type: "INTEGER", nullable: true),
                    MatchMethod = table.Column<int>(type: "INTEGER", nullable: true),
                    MatchScore = table.Column<double>(type: "REAL", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_node_series_links", x => x.Id);
                    table.ForeignKey(
                        name: "FK_node_series_links_catalog_nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "catalog_nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_node_series_links_libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_node_series_links_metadata_records_RecordId",
                        column: x => x.RecordId,
                        principalTable: "metadata_records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_embedded_metadata_State",
                table: "embedded_metadata",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_folder_metadata_precedence_NodeId",
                table: "folder_metadata_precedence",
                column: "NodeId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_metadata_records_Provider_ExternalId",
                table: "metadata_records",
                columns: new[] { "Provider", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_metadata_records_PublicId",
                table: "metadata_records",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_node_series_links_LibraryId_State",
                table: "node_series_links",
                columns: new[] { "LibraryId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_node_series_links_NodeId",
                table: "node_series_links",
                column: "NodeId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_node_series_links_RecordId",
                table: "node_series_links",
                column: "RecordId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "embedded_metadata");

            migrationBuilder.DropTable(
                name: "folder_metadata_precedence");

            migrationBuilder.DropTable(
                name: "node_series_links");

            migrationBuilder.DropTable(
                name: "metadata_records");

            migrationBuilder.DropColumn(
                name: "MetadataEnabled",
                table: "libraries");

            migrationBuilder.DropColumn(
                name: "MetadataPrecedence",
                table: "libraries");

            migrationBuilder.DropColumn(
                name: "MetadataSeriesInfoHidden",
                table: "libraries");

            migrationBuilder.DropColumn(
                name: "MetadataBackoffStep",
                table: "app_settings");

            migrationBuilder.DropColumn(
                name: "MetadataBackoffUntil",
                table: "app_settings");

            migrationBuilder.DropColumn(
                name: "MetadataBudgetDayUtc",
                table: "app_settings");

            migrationBuilder.DropColumn(
                name: "MetadataBudgetUsed",
                table: "app_settings");

            migrationBuilder.DropColumn(
                name: "MetadataConsentAt",
                table: "app_settings");

            migrationBuilder.DropColumn(
                name: "MetadataConsentVersion",
                table: "app_settings");

            migrationBuilder.DropColumn(
                name: "MetadataDailyBudget",
                table: "app_settings");

            migrationBuilder.DropColumn(
                name: "MetadataEnabled",
                table: "app_settings");

            migrationBuilder.DropColumn(
                name: "MetadataLastErrorAt",
                table: "app_settings");

            migrationBuilder.DropColumn(
                name: "MetadataLastErrorCode",
                table: "app_settings");

            migrationBuilder.DropColumn(
                name: "MetadataSeriesInfoHidden",
                table: "app_settings");
        }
    }
}
