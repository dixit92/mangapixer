using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace com.lifepixer.mangapixer.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTrackerSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tracker_accounts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    Tracker = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    RemoteUserId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    AccessTokenProtected = table.Column<string>(type: "TEXT", maxLength: 8192, nullable: true),
                    RefreshTokenProtected = table.Column<string>(type: "TEXT", maxLength: 8192, nullable: true),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: true),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    ConsentVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    ConsentAt = table.Column<long>(type: "INTEGER", nullable: false),
                    AddStartedSeries = table.Column<bool>(type: "INTEGER", nullable: false),
                    PrivateEntries = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSuccessAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastFailureAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastFailureCode = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    RequestsDay = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    RequestsUsed = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tracker_accounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tracker_accounts_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tracker_entries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    Tracker = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    RecordId = table.Column<long>(type: "INTEGER", nullable: false),
                    RemoteId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    RemoteIdSource = table.Column<int>(type: "INTEGER", nullable: true),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    ErrorCode = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    DontSync = table.Column<bool>(type: "INTEGER", nullable: false),
                    LocalChapters = table.Column<int>(type: "INTEGER", nullable: true),
                    LocalVolumes = table.Column<int>(type: "INTEGER", nullable: true),
                    HeldChapters = table.Column<int>(type: "INTEGER", nullable: true),
                    HeldVolumes = table.Column<int>(type: "INTEGER", nullable: true),
                    LastSentChapters = table.Column<int>(type: "INTEGER", nullable: true),
                    LastSentVolumes = table.Column<int>(type: "INTEGER", nullable: true),
                    LastSentStatus = table.Column<int>(type: "INTEGER", nullable: true),
                    LastSentAt = table.Column<long>(type: "INTEGER", nullable: true),
                    RemoteChapters = table.Column<int>(type: "INTEGER", nullable: true),
                    RemoteVolumes = table.Column<int>(type: "INTEGER", nullable: true),
                    RemoteStatus = table.Column<int>(type: "INTEGER", nullable: true),
                    RemoteMissing = table.Column<bool>(type: "INTEGER", nullable: false),
                    RemoteSeenAt = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tracker_entries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tracker_entries_metadata_records_RecordId",
                        column: x => x.RecordId,
                        principalTable: "metadata_records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_tracker_entries_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tracker_id_overrides",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RecordId = table.Column<long>(type: "INTEGER", nullable: false),
                    Tracker = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    RemoteId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SetAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tracker_id_overrides", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tracker_id_overrides_metadata_records_RecordId",
                        column: x => x.RecordId,
                        principalTable: "metadata_records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tracker_node_overrides",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    NodeId = table.Column<long>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tracker_node_overrides", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tracker_node_overrides_catalog_nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "catalog_nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_tracker_node_overrides_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tracker_oauth_states",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    Tracker = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    StateHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SessionHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    VerifierProtected = table.Column<string>(type: "TEXT", maxLength: 8192, nullable: false),
                    ConsentVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tracker_oauth_states", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tracker_oauth_states_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tracker_retries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    Tracker = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    RecordId = table.Column<long>(type: "INTEGER", nullable: false),
                    SendHeld = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotBefore = table.Column<long>(type: "INTEGER", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tracker_retries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tracker_retries_metadata_records_RecordId",
                        column: x => x.RecordId,
                        principalTable: "metadata_records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_tracker_retries_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tracker_settings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Tracker = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ConsentVersion = table.Column<int>(type: "INTEGER", nullable: true),
                    ConsentAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ClientId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ClientSecretProtected = table.Column<string>(type: "TEXT", maxLength: 8192, nullable: true),
                    RedirectUri = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tracker_settings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tracker_accounts_UserId_Tracker",
                table: "tracker_accounts",
                columns: new[] { "UserId", "Tracker" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tracker_entries_RecordId",
                table: "tracker_entries",
                column: "RecordId");

            migrationBuilder.CreateIndex(
                name: "IX_tracker_entries_UserId_Tracker_RecordId",
                table: "tracker_entries",
                columns: new[] { "UserId", "Tracker", "RecordId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tracker_id_overrides_RecordId_Tracker",
                table: "tracker_id_overrides",
                columns: new[] { "RecordId", "Tracker" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tracker_node_overrides_NodeId",
                table: "tracker_node_overrides",
                column: "NodeId");

            migrationBuilder.CreateIndex(
                name: "IX_tracker_node_overrides_UserId_NodeId",
                table: "tracker_node_overrides",
                columns: new[] { "UserId", "NodeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tracker_oauth_states_ExpiresAt",
                table: "tracker_oauth_states",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_tracker_oauth_states_StateHash",
                table: "tracker_oauth_states",
                column: "StateHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tracker_oauth_states_UserId",
                table: "tracker_oauth_states",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_tracker_retries_NotBefore",
                table: "tracker_retries",
                column: "NotBefore");

            migrationBuilder.CreateIndex(
                name: "IX_tracker_retries_RecordId",
                table: "tracker_retries",
                column: "RecordId");

            migrationBuilder.CreateIndex(
                name: "IX_tracker_retries_UserId_Tracker_RecordId",
                table: "tracker_retries",
                columns: new[] { "UserId", "Tracker", "RecordId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tracker_settings_Tracker",
                table: "tracker_settings",
                column: "Tracker",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tracker_accounts");

            migrationBuilder.DropTable(
                name: "tracker_entries");

            migrationBuilder.DropTable(
                name: "tracker_id_overrides");

            migrationBuilder.DropTable(
                name: "tracker_node_overrides");

            migrationBuilder.DropTable(
                name: "tracker_oauth_states");

            migrationBuilder.DropTable(
                name: "tracker_retries");

            migrationBuilder.DropTable(
                name: "tracker_settings");
        }
    }
}
