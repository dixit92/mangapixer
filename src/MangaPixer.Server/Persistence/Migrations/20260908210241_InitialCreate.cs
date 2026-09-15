using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace com.lifepixer.mangaplex.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ActorUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    TargetUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    TargetLibraryId = table.Column<long>(type: "INTEGER", nullable: true),
                    TargetItemId = table.Column<long>(type: "INTEGER", nullable: true),
                    Action = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Result = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Timestamp = table.Column<long>(type: "INTEGER", nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "cache_entries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CacheKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CacheVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    CacheFilePath = table.Column<string>(type: "TEXT", nullable: false),
                    ByteSize = table.Column<long>(type: "INTEGER", nullable: false),
                    MediaType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastAccessedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cache_entries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "jobs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    LibraryId = table.Column<long>(type: "INTEGER", nullable: true),
                    ItemId = table.Column<long>(type: "INTEGER", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    LeaseOwner = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    LeaseExpiry = table.Column<long>(type: "INTEGER", nullable: true),
                    Attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    ProgressCurrent = table.Column<int>(type: "INTEGER", nullable: true),
                    ProgressTotal = table.Column<int>(type: "INTEGER", nullable: true),
                    SanitizedError = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    QueuedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_jobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "libraries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PublicId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    RootPath = table.Column<string>(type: "TEXT", nullable: false),
                    CaseComparisonPolicy = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    RootIdentity = table.Column<string>(type: "TEXT", nullable: true),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CatalogRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    ScanSchedule = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastScanCompleted = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_libraries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "scan_observations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ScanRunId = table.Column<long>(type: "INTEGER", nullable: false),
                    LibraryId = table.Column<long>(type: "INTEGER", nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", nullable: false),
                    PathKey = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    ParentPathKey = table.Column<string>(type: "TEXT", nullable: false),
                    ParentNodeId = table.Column<long>(type: "INTEGER", nullable: true),
                    ByteLength = table.Column<long>(type: "INTEGER", nullable: false),
                    ModificationTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    FileIdentity = table.Column<string>(type: "TEXT", nullable: true),
                    ObservationStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    SanitizedError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scan_observations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "scan_runs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LibraryId = table.Column<long>(type: "INTEGER", nullable: false),
                    ScanRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    LeaseOwner = table.Column<string>(type: "TEXT", nullable: true),
                    LeaseExpiry = table.Column<long>(type: "INTEGER", nullable: true),
                    NodesObserved = table.Column<int>(type: "INTEGER", nullable: false),
                    NodesAdded = table.Column<int>(type: "INTEGER", nullable: false),
                    NodesUpdated = table.Column<int>(type: "INTEGER", nullable: false),
                    NodesTombstoned = table.Column<int>(type: "INTEGER", nullable: false),
                    SanitizedError = table.Column<string>(type: "TEXT", nullable: true),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scan_runs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PublicId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    NormalizedUserName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: false),
                    SecurityStamp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsAdmin = table.Column<bool>(type: "INTEGER", nullable: false),
                    ForcePasswordChange = table.Column<bool>(type: "INTEGER", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LockoutEnd = table.Column<long>(type: "INTEGER", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastLoginAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "catalog_nodes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PublicId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    LibraryId = table.Column<long>(type: "INTEGER", nullable: false),
                    ParentId = table.Column<long>(type: "INTEGER", nullable: true),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", nullable: false),
                    PathKey = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    SortKey = table.Column<string>(type: "TEXT", nullable: false),
                    Availability = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSeenScanRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catalog_nodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_catalog_nodes_catalog_nodes_ParentId",
                        column: x => x.ParentId,
                        principalTable: "catalog_nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_catalog_nodes_libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "bookmarks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    ItemId = table.Column<long>(type: "INTEGER", nullable: false),
                    ContentVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    EntryKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    NormalizedAnchor = table.Column<double>(type: "REAL", nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bookmarks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_bookmarks_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "item_reader_overrides",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    ItemId = table.Column<long>(type: "INTEGER", nullable: false),
                    ReaderMode = table.Column<int>(type: "INTEGER", nullable: true),
                    Direction = table.Column<int>(type: "INTEGER", nullable: true),
                    FitMode = table.Column<int>(type: "INTEGER", nullable: true),
                    SpreadOffset = table.Column<int>(type: "INTEGER", nullable: true),
                    CoverOffset = table.Column<int>(type: "INTEGER", nullable: true),
                    Background = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_item_reader_overrides", x => x.Id);
                    table.ForeignKey(
                        name: "FK_item_reader_overrides_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "library_grants",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    LibraryId = table.Column<long>(type: "INTEGER", nullable: false),
                    GrantedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_library_grants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_library_grants_libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_library_grants_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "reader_preferences",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    DefaultReaderMode = table.Column<int>(type: "INTEGER", nullable: false),
                    PreferDoubleSpread = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReducedMotion = table.Column<bool>(type: "INTEGER", nullable: false),
                    PreferredBackground = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reader_preferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_reader_preferences_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "reading_progress",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    ItemId = table.Column<long>(type: "INTEGER", nullable: false),
                    ContentVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    EntryKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    NormalizedAnchor = table.Column<double>(type: "REAL", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    LastMutationId = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reading_progress", x => x.Id);
                    table.ForeignKey(
                        name: "FK_reading_progress_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sessions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TicketId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    SecurityStamp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    IssuedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    IsRevoked = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sessions_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "archive_items",
                columns: table => new
                {
                    NodeId = table.Column<long>(type: "INTEGER", nullable: false),
                    ArchiveFormat = table.Column<int>(type: "INTEGER", nullable: false),
                    ByteLength = table.Column<long>(type: "INTEGER", nullable: false),
                    ModificationTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    FileIdentity = table.Column<string>(type: "TEXT", nullable: true),
                    ContentVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    AnalysisState = table.Column<int>(type: "INTEGER", nullable: false),
                    AnalysisError = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    PageCount = table.Column<int>(type: "INTEGER", nullable: true),
                    StrongHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    StrongHashSourceVersion = table.Column<long>(type: "INTEGER", nullable: true),
                    LastAnalyzedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_archive_items", x => x.NodeId);
                    table.ForeignKey(
                        name: "FK_archive_items_catalog_nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "catalog_nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "page_entries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ItemId = table.Column<long>(type: "INTEGER", nullable: false),
                    ContentVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    EntryKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SourceEntryLocator = table.Column<string>(type: "TEXT", nullable: false),
                    MediaType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Width = table.Column<int>(type: "INTEGER", nullable: true),
                    Height = table.Column<int>(type: "INTEGER", nullable: true),
                    AnimationState = table.Column<int>(type: "INTEGER", nullable: false),
                    PageState = table.Column<int>(type: "INTEGER", nullable: false),
                    ByteSize = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_page_entries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_page_entries_archive_items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "archive_items",
                        principalColumn: "NodeId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_archive_items_AnalysisState",
                table: "archive_items",
                column: "AnalysisState");

            migrationBuilder.CreateIndex(
                name: "IX_archive_items_ContentVersion",
                table: "archive_items",
                column: "ContentVersion");

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_ActorUserId",
                table: "audit_events",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_Timestamp",
                table: "audit_events",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_bookmarks_UserId_ItemId",
                table: "bookmarks",
                columns: new[] { "UserId", "ItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_cache_entries_CacheKey",
                table: "cache_entries",
                column: "CacheKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cache_entries_State_LastAccessedAt",
                table: "cache_entries",
                columns: new[] { "State", "LastAccessedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_catalog_nodes_LibraryId",
                table: "catalog_nodes",
                column: "LibraryId");

            migrationBuilder.CreateIndex(
                name: "IX_catalog_nodes_LibraryId_PathKey",
                table: "catalog_nodes",
                columns: new[] { "LibraryId", "PathKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_catalog_nodes_ParentId_Kind_SortKey",
                table: "catalog_nodes",
                columns: new[] { "ParentId", "Kind", "SortKey" });

            migrationBuilder.CreateIndex(
                name: "IX_catalog_nodes_PublicId",
                table: "catalog_nodes",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_item_reader_overrides_UserId_ItemId",
                table: "item_reader_overrides",
                columns: new[] { "UserId", "ItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_jobs_LibraryId",
                table: "jobs",
                column: "LibraryId");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_Status_LeaseExpiry",
                table: "jobs",
                columns: new[] { "Status", "LeaseExpiry" });

            migrationBuilder.CreateIndex(
                name: "IX_libraries_PublicId",
                table: "libraries",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_library_grants_LibraryId",
                table: "library_grants",
                column: "LibraryId");

            migrationBuilder.CreateIndex(
                name: "IX_library_grants_UserId_LibraryId",
                table: "library_grants",
                columns: new[] { "UserId", "LibraryId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_page_entries_ItemId",
                table: "page_entries",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_page_entries_ItemId_ContentVersion_EntryKey",
                table: "page_entries",
                columns: new[] { "ItemId", "ContentVersion", "EntryKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_page_entries_ItemId_ContentVersion_Ordinal",
                table: "page_entries",
                columns: new[] { "ItemId", "ContentVersion", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_reader_preferences_UserId",
                table: "reader_preferences",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_reading_progress_UserId_ItemId",
                table: "reading_progress",
                columns: new[] { "UserId", "ItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_reading_progress_UserId_UpdatedAt",
                table: "reading_progress",
                columns: new[] { "UserId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_scan_observations_LibraryId_PathKey",
                table: "scan_observations",
                columns: new[] { "LibraryId", "PathKey" });

            migrationBuilder.CreateIndex(
                name: "IX_scan_observations_ScanRunId",
                table: "scan_observations",
                column: "ScanRunId");

            migrationBuilder.CreateIndex(
                name: "IX_scan_runs_LibraryId",
                table: "scan_runs",
                column: "LibraryId");

            migrationBuilder.CreateIndex(
                name: "IX_scan_runs_Status",
                table: "scan_runs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_sessions_ExpiresAt",
                table: "sessions",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_sessions_TicketId",
                table: "sessions",
                column: "TicketId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sessions_UserId",
                table: "sessions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_users_NormalizedUserName",
                table: "users",
                column: "NormalizedUserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_PublicId",
                table: "users",
                column: "PublicId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "bookmarks");

            migrationBuilder.DropTable(
                name: "cache_entries");

            migrationBuilder.DropTable(
                name: "item_reader_overrides");

            migrationBuilder.DropTable(
                name: "jobs");

            migrationBuilder.DropTable(
                name: "library_grants");

            migrationBuilder.DropTable(
                name: "page_entries");

            migrationBuilder.DropTable(
                name: "reader_preferences");

            migrationBuilder.DropTable(
                name: "reading_progress");

            migrationBuilder.DropTable(
                name: "scan_observations");

            migrationBuilder.DropTable(
                name: "scan_runs");

            migrationBuilder.DropTable(
                name: "sessions");

            migrationBuilder.DropTable(
                name: "archive_items");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "catalog_nodes");

            migrationBuilder.DropTable(
                name: "libraries");
        }
    }
}
