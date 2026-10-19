using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace com.lifepixer.mangapixer.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMangaDexLatestUpload : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "freshness_state",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    CheckHours = table.Column<int>(type: "INTEGER", nullable: true),
                    SweepStartedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    SweepFinishedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    SweepOutcome = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    SweepRequests = table.Column<int>(type: "INTEGER", nullable: false),
                    SweepRecords = table.Column<int>(type: "INTEGER", nullable: false),
                    SweepChanged = table.Column<int>(type: "INTEGER", nullable: false),
                    FeedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    FeedOutcome = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    FeedRequests = table.Column<int>(type: "INTEGER", nullable: false),
                    FeedMatched = table.Column<int>(type: "INTEGER", nullable: false),
                    DuePassAt = table.Column<long>(type: "INTEGER", nullable: true),
                    BackfillAt = table.Column<long>(type: "INTEGER", nullable: true),
                    BackfillDoneAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_freshness_state", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "library_freshness",
                columns: table => new
                {
                    LibraryId = table.Column<long>(type: "INTEGER", nullable: false),
                    MangaDexSweepAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ExportMangaDexSweepAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_library_freshness", x => x.LibraryId);
                    table.ForeignKey(
                        name: "FK_library_freshness_libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mangadex_latest_uploads",
                columns: table => new
                {
                    RecordId = table.Column<long>(type: "INTEGER", nullable: false),
                    ChapterId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    SeenAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CheckedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mangadex_latest_uploads", x => x.RecordId);
                    table.ForeignKey(
                        name: "FK_mangadex_latest_uploads_metadata_records_RecordId",
                        column: x => x.RecordId,
                        principalTable: "metadata_records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "metadata_refresh_nudges",
                columns: table => new
                {
                    RecordId = table.Column<long>(type: "INTEGER", nullable: false),
                    At = table.Column<long>(type: "INTEGER", nullable: false),
                    Reason = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_metadata_refresh_nudges", x => x.RecordId);
                    table.ForeignKey(
                        name: "FK_metadata_refresh_nudges_metadata_records_RecordId",
                        column: x => x.RecordId,
                        principalTable: "metadata_records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "freshness_state");

            migrationBuilder.DropTable(
                name: "library_freshness");

            migrationBuilder.DropTable(
                name: "mangadex_latest_uploads");

            migrationBuilder.DropTable(
                name: "metadata_refresh_nudges");
        }
    }
}
