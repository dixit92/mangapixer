using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace com.lifepixer.mangapixer.Server.Persistence.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// The 1.29.0 cycle's one migration, additive only (virtual volumes and volume covers): the tables
    /// <c>metadata_companions</c> (MangaDex / AniList companion of a linked series record), <c>series_volume_maps</c>
    /// (volume -> chapters per series record and source), <c>volume_covers</c> (known web covers), <c>node_cover_choices</c>
    /// (the admin's per-node cover choice), <c>node_auto_covers</c> (the automatic layer's decision per node),
    /// <c>metadata_provider_state</c> (per-provider backoff) and <c>folder_view_settings</c> (per-folder Volumes view); the
    /// columns <c>app_settings.MetadataCoverLanguage</c> ("en"), <c>MetadataVolumeCoversEnabled</c>, <c>CoverSpreadCropEnabled</c>,
    /// <c>VirtualVolumesEnabled</c> (NOT NULL, default TRUE so the existing row reads them as on),
    /// <c>libraries.VirtualVolumes</c> (null = inherit), <c>libraries.WebCoversHidden</c> (default FALSE = shown) and
    /// <c>reader_preferences.SeriesViewMode</c> (null = follow the default).
    /// </summary>
    public partial class AddVolumeCoversAndVirtualVolumes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SeriesViewMode",
                table: "reader_preferences",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VirtualVolumes",
                table: "libraries",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WebCoversHidden",
                table: "libraries",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CoverSpreadCropEnabled",
                table: "app_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "MetadataCoverLanguage",
                table: "app_settings",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "en");

            migrationBuilder.AddColumn<bool>(
                name: "MetadataVolumeCoversEnabled",
                table: "app_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "VirtualVolumesEnabled",
                table: "app_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "folder_view_settings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    NodeId = table.Column<long>(type: "INTEGER", nullable: false),
                    VirtualVolumes = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_folder_view_settings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_folder_view_settings_catalog_nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "catalog_nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "metadata_companions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RecordId = table.Column<long>(type: "INTEGER", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CompanionRecordId = table.Column<long>(type: "INTEGER", nullable: true),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Method = table.Column<int>(type: "INTEGER", nullable: false),
                    CheckedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    NextCheckAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_metadata_companions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_metadata_companions_metadata_records_CompanionRecordId",
                        column: x => x.CompanionRecordId,
                        principalTable: "metadata_records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_metadata_companions_metadata_records_RecordId",
                        column: x => x.RecordId,
                        principalTable: "metadata_records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "metadata_provider_state",
                columns: table => new
                {
                    Provider = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    BackoffUntil = table.Column<long>(type: "INTEGER", nullable: true),
                    BackoffStep = table.Column<int>(type: "INTEGER", nullable: false),
                    LastErrorAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastErrorCode = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_metadata_provider_state", x => x.Provider);
                });

            migrationBuilder.CreateTable(
                name: "series_volume_maps",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RecordId = table.Column<long>(type: "INTEGER", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    VolumesJson = table.Column<string>(type: "TEXT", nullable: true),
                    UnassignedJson = table.Column<string>(type: "TEXT", nullable: true),
                    ChaptersPerVolume = table.Column<double>(type: "REAL", nullable: true),
                    KnownVolumeCount = table.Column<int>(type: "INTEGER", nullable: true),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    FetchedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    NextCheckAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_series_volume_maps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_series_volume_maps_metadata_records_RecordId",
                        column: x => x.RecordId,
                        principalTable: "metadata_records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "volume_covers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PublicId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ProviderRecordId = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Volume = table.Column<int>(type: "INTEGER", nullable: true),
                    Variant = table.Column<int>(type: "INTEGER", nullable: false),
                    Locale = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    RemoteId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    RemoteFile = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    RemoteUpdatedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    StoredVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    Hash = table.Column<long>(type: "INTEGER", nullable: true),
                    Width = table.Column<int>(type: "INTEGER", nullable: true),
                    Height = table.Column<int>(type: "INTEGER", nullable: true),
                    ListedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    StoredAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_volume_covers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_volume_covers_metadata_records_ProviderRecordId",
                        column: x => x.ProviderRecordId,
                        principalTable: "metadata_records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "node_auto_covers",
                columns: table => new
                {
                    NodeId = table.Column<long>(type: "INTEGER", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    CropSide = table.Column<int>(type: "INTEGER", nullable: true),
                    VolumeCoverId = table.Column<long>(type: "INTEGER", nullable: true),
                    Reason = table.Column<int>(type: "INTEGER", nullable: false),
                    LocalHash = table.Column<long>(type: "INTEGER", nullable: true),
                    InputsKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    RecheckAt = table.Column<long>(type: "INTEGER", nullable: true),
                    DecidedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_node_auto_covers", x => x.NodeId);
                    table.ForeignKey(
                        name: "FK_node_auto_covers_catalog_nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "catalog_nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_node_auto_covers_volume_covers_VolumeCoverId",
                        column: x => x.VolumeCoverId,
                        principalTable: "volume_covers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "node_cover_choices",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    NodeId = table.Column<long>(type: "INTEGER", nullable: false),
                    Mode = table.Column<int>(type: "INTEGER", nullable: false),
                    ArchiveNodeId = table.Column<long>(type: "INTEGER", nullable: true),
                    VolumeCoverId = table.Column<long>(type: "INTEGER", nullable: true),
                    CropSide = table.Column<int>(type: "INTEGER", nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    SetByUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    SetAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_node_cover_choices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_node_cover_choices_catalog_nodes_ArchiveNodeId",
                        column: x => x.ArchiveNodeId,
                        principalTable: "catalog_nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_node_cover_choices_catalog_nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "catalog_nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_node_cover_choices_users_SetByUserId",
                        column: x => x.SetByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_node_cover_choices_volume_covers_VolumeCoverId",
                        column: x => x.VolumeCoverId,
                        principalTable: "volume_covers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_folder_view_settings_NodeId",
                table: "folder_view_settings",
                column: "NodeId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_metadata_companions_CompanionRecordId",
                table: "metadata_companions",
                column: "CompanionRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_metadata_companions_RecordId_Provider",
                table: "metadata_companions",
                columns: new[] { "RecordId", "Provider" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_metadata_companions_State_NextCheckAt",
                table: "metadata_companions",
                columns: new[] { "State", "NextCheckAt" });

            migrationBuilder.CreateIndex(
                name: "IX_node_auto_covers_RecheckAt",
                table: "node_auto_covers",
                column: "RecheckAt");

            migrationBuilder.CreateIndex(
                name: "IX_node_auto_covers_VolumeCoverId",
                table: "node_auto_covers",
                column: "VolumeCoverId");

            migrationBuilder.CreateIndex(
                name: "IX_node_cover_choices_ArchiveNodeId",
                table: "node_cover_choices",
                column: "ArchiveNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_node_cover_choices_NodeId",
                table: "node_cover_choices",
                column: "NodeId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_node_cover_choices_SetByUserId",
                table: "node_cover_choices",
                column: "SetByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_node_cover_choices_VolumeCoverId",
                table: "node_cover_choices",
                column: "VolumeCoverId");

            migrationBuilder.CreateIndex(
                name: "IX_series_volume_maps_RecordId_Source",
                table: "series_volume_maps",
                columns: new[] { "RecordId", "Source" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_volume_covers_ProviderRecordId_Kind_Volume_Variant_Locale",
                table: "volume_covers",
                columns: new[] { "ProviderRecordId", "Kind", "Volume", "Variant", "Locale" });

            migrationBuilder.CreateIndex(
                name: "IX_volume_covers_ProviderRecordId_RemoteId",
                table: "volume_covers",
                columns: new[] { "ProviderRecordId", "RemoteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_volume_covers_PublicId",
                table: "volume_covers",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_volume_covers_State",
                table: "volume_covers",
                column: "State");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "folder_view_settings");

            migrationBuilder.DropTable(
                name: "metadata_companions");

            migrationBuilder.DropTable(
                name: "metadata_provider_state");

            migrationBuilder.DropTable(
                name: "node_auto_covers");

            migrationBuilder.DropTable(
                name: "node_cover_choices");

            migrationBuilder.DropTable(
                name: "series_volume_maps");

            migrationBuilder.DropTable(
                name: "volume_covers");

            migrationBuilder.DropColumn(
                name: "SeriesViewMode",
                table: "reader_preferences");

            migrationBuilder.DropColumn(
                name: "VirtualVolumes",
                table: "libraries");

            migrationBuilder.DropColumn(
                name: "WebCoversHidden",
                table: "libraries");

            migrationBuilder.DropColumn(
                name: "CoverSpreadCropEnabled",
                table: "app_settings");

            migrationBuilder.DropColumn(
                name: "MetadataCoverLanguage",
                table: "app_settings");

            migrationBuilder.DropColumn(
                name: "MetadataVolumeCoversEnabled",
                table: "app_settings");

            migrationBuilder.DropColumn(
                name: "VirtualVolumesEnabled",
                table: "app_settings");
        }
    }
}
