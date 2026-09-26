using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace com.lifepixer.mangapixer.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMetadataAutoMatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PublicationsJson",
                table: "metadata_records",
                type: "TEXT",
                maxLength: 8192,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RelationsJson",
                table: "metadata_records",
                type: "TEXT",
                maxLength: 8192,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MetadataAutoConsentAt",
                table: "app_settings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MetadataAutoConsentVersion",
                table: "app_settings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MetadataAutoMatchEnabled",
                table: "app_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "MetadataAutoTitleThreshold",
                table: "app_settings",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MetadataMarginThreshold",
                table: "app_settings",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MetadataRefreshDayUtc",
                table: "app_settings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MetadataRefreshUsed",
                table: "app_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "MetadataReviewFloorThreshold",
                table: "app_settings",
                type: "REAL",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "folder_metadata_content",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    NodeId = table.Column<long>(type: "INTEGER", nullable: false),
                    Content = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_folder_metadata_content", x => x.Id);
                    table.ForeignKey(
                        name: "FK_folder_metadata_content_catalog_nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "catalog_nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "metadata_flags",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PublicId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    NodeId = table.Column<long>(type: "INTEGER", nullable: false),
                    LibraryId = table.Column<long>(type: "INTEGER", nullable: false),
                    ReporterUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Reason = table.Column<int>(type: "INTEGER", nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ResolvedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ResolvedByUserId = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_metadata_flags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_metadata_flags_catalog_nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "catalog_nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_metadata_flags_libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_metadata_flags_users_ReporterUserId",
                        column: x => x.ReporterUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_metadata_flags_users_ResolvedByUserId",
                        column: x => x.ResolvedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "metadata_match_candidates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    NodeId = table.Column<long>(type: "INTEGER", nullable: false),
                    Rank = table.Column<int>(type: "INTEGER", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    ProviderType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Format = table.Column<int>(type: "INTEGER", nullable: true),
                    Origin = table.Column<int>(type: "INTEGER", nullable: true),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    Volumes = table.Column<int>(type: "INTEGER", nullable: true),
                    TitleScore = table.Column<double>(type: "REAL", nullable: false),
                    AdjustedScore = table.Column<double>(type: "REAL", nullable: false),
                    Reasons = table.Column<int>(type: "INTEGER", nullable: false),
                    ImageRemoteUrl = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_metadata_match_candidates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_metadata_match_candidates_catalog_nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "catalog_nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "metadata_match_runs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PublicId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    LibraryId = table.Column<long>(type: "INTEGER", nullable: false),
                    Trigger = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ReviewFirst = table.Column<bool>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Candidates = table.Column<int>(type: "INTEGER", nullable: false),
                    Queued = table.Column<int>(type: "INTEGER", nullable: false),
                    Processed = table.Column<int>(type: "INTEGER", nullable: false),
                    AutoLinked = table.Column<int>(type: "INTEGER", nullable: false),
                    NeedsReview = table.Column<int>(type: "INTEGER", nullable: false),
                    Unmatched = table.Column<int>(type: "INTEGER", nullable: false),
                    Skipped = table.Column<int>(type: "INTEGER", nullable: false),
                    Failed = table.Column<int>(type: "INTEGER", nullable: false),
                    RequestsUsed = table.Column<int>(type: "INTEGER", nullable: false),
                    AutoChangedByAdmin = table.Column<int>(type: "INTEGER", nullable: false),
                    ReviewAcceptedTop = table.Column<int>(type: "INTEGER", nullable: false),
                    ReviewAcceptedOther = table.Column<int>(type: "INTEGER", nullable: false),
                    ReviewDontMatch = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_metadata_match_runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_metadata_match_runs_libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "metadata_match_queue",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    NodeId = table.Column<long>(type: "INTEGER", nullable: false),
                    LibraryId = table.Column<long>(type: "INTEGER", nullable: false),
                    Reason = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Level = table.Column<int>(type: "INTEGER", nullable: false),
                    WorkClass = table.Column<int>(type: "INTEGER", nullable: true),
                    MemberNodeIdsJson = table.Column<string>(type: "TEXT", maxLength: 16384, nullable: true),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    NotBefore = table.Column<long>(type: "INTEGER", nullable: true),
                    LeaseUntil = table.Column<long>(type: "INTEGER", nullable: true),
                    LeaseOwner = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    LastErrorCode = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    RunId = table.Column<long>(type: "INTEGER", nullable: true),
                    ReviewFirst = table.Column<bool>(type: "INTEGER", nullable: false),
                    EnqueuedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Outcome = table.Column<int>(type: "INTEGER", nullable: true),
                    OutcomeReasons = table.Column<int>(type: "INTEGER", nullable: false),
                    RetryStep = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_metadata_match_queue", x => x.Id);
                    table.ForeignKey(
                        name: "FK_metadata_match_queue_catalog_nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "catalog_nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_metadata_match_queue_libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_metadata_match_queue_metadata_match_runs_RunId",
                        column: x => x.RunId,
                        principalTable: "metadata_match_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_folder_metadata_content_NodeId",
                table: "folder_metadata_content",
                column: "NodeId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_metadata_flags_LibraryId",
                table: "metadata_flags",
                column: "LibraryId");

            migrationBuilder.CreateIndex(
                name: "IX_metadata_flags_NodeId",
                table: "metadata_flags",
                column: "NodeId");

            migrationBuilder.CreateIndex(
                name: "IX_metadata_flags_open_per_reporter",
                table: "metadata_flags",
                columns: new[] { "ReporterUserId", "NodeId" },
                unique: true,
                filter: "\"State\" = 0");

            migrationBuilder.CreateIndex(
                name: "IX_metadata_flags_PublicId",
                table: "metadata_flags",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_metadata_flags_ReporterUserId_CreatedAt",
                table: "metadata_flags",
                columns: new[] { "ReporterUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_metadata_flags_ResolvedByUserId",
                table: "metadata_flags",
                column: "ResolvedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_metadata_flags_State_CreatedAt",
                table: "metadata_flags",
                columns: new[] { "State", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_metadata_match_candidates_NodeId_Rank",
                table: "metadata_match_candidates",
                columns: new[] { "NodeId", "Rank" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_metadata_match_queue_LibraryId_Outcome",
                table: "metadata_match_queue",
                columns: new[] { "LibraryId", "Outcome" });

            migrationBuilder.CreateIndex(
                name: "IX_metadata_match_queue_NodeId",
                table: "metadata_match_queue",
                column: "NodeId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_metadata_match_queue_RunId",
                table: "metadata_match_queue",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_metadata_match_queue_State_NotBefore",
                table: "metadata_match_queue",
                columns: new[] { "State", "NotBefore" });

            migrationBuilder.CreateIndex(
                name: "IX_metadata_match_runs_LibraryId_StartedAt",
                table: "metadata_match_runs",
                columns: new[] { "LibraryId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_metadata_match_runs_PublicId",
                table: "metadata_match_runs",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_metadata_match_runs_Status",
                table: "metadata_match_runs",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "folder_metadata_content");

            migrationBuilder.DropTable(
                name: "metadata_flags");

            migrationBuilder.DropTable(
                name: "metadata_match_candidates");

            migrationBuilder.DropTable(
                name: "metadata_match_queue");

            migrationBuilder.DropTable(
                name: "metadata_match_runs");

            migrationBuilder.DropColumn(
                name: "PublicationsJson",
                table: "metadata_records");

            migrationBuilder.DropColumn(
                name: "RelationsJson",
                table: "metadata_records");

            migrationBuilder.DropColumn(
                name: "MetadataAutoConsentAt",
                table: "app_settings");

            migrationBuilder.DropColumn(
                name: "MetadataAutoConsentVersion",
                table: "app_settings");

            migrationBuilder.DropColumn(
                name: "MetadataAutoMatchEnabled",
                table: "app_settings");

            migrationBuilder.DropColumn(
                name: "MetadataAutoTitleThreshold",
                table: "app_settings");

            migrationBuilder.DropColumn(
                name: "MetadataMarginThreshold",
                table: "app_settings");

            migrationBuilder.DropColumn(
                name: "MetadataRefreshDayUtc",
                table: "app_settings");

            migrationBuilder.DropColumn(
                name: "MetadataRefreshUsed",
                table: "app_settings");

            migrationBuilder.DropColumn(
                name: "MetadataReviewFloorThreshold",
                table: "app_settings");
        }
    }
}
