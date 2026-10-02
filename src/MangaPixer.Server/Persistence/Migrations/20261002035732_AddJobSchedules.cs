using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace com.lifepixer.mangapixer.Server.Persistence.Migrations
{
    /// <summary>
    /// 1.32.0 scheduled jobs and refresh cadence (lane D): <c>job_runs</c> (persisted last runs), <c>metadata_record_observations</c>
    /// (the publishing pace), the job hours and the cadence choice on <c>app_settings</c> (<c>MetadataRefreshFollowPace</c> NOT NULL,
    /// default TRUE so the existing row follows the pace), a time of day for scans on <c>libraries</c>, and the per-record
    /// <c>metadata_records.RefreshCadenceDays</c>. Purely additive.
    /// </summary>
    public partial class AddJobSchedules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RefreshCadenceDays",
                table: "metadata_records",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ScanHour",
                table: "libraries",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ScanWeekday",
                table: "libraries",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BackupHour",
                table: "app_settings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CacheEvictionHour",
                table: "app_settings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MetadataRefreshFinishedDays",
                table: "app_settings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MetadataRefreshFollowPace",
                table: "app_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "MetadataRefreshHour",
                table: "app_settings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MetadataRefreshOngoingDays",
                table: "app_settings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "job_runs",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    LastStartedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastFinishedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastOutcome = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    LastDetail = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    LastDurationMs = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job_runs", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "metadata_record_observations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RecordId = table.Column<long>(type: "INTEGER", nullable: false),
                    ObservedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LatestChapter = table.Column<double>(type: "REAL", nullable: true),
                    OriginVolumes = table.Column<int>(type: "INTEGER", nullable: true),
                    OriginStatus = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_metadata_record_observations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_metadata_record_observations_metadata_records_RecordId",
                        column: x => x.RecordId,
                        principalTable: "metadata_records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_metadata_record_observations_RecordId_ObservedAt",
                table: "metadata_record_observations",
                columns: new[] { "RecordId", "ObservedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "job_runs");

            migrationBuilder.DropTable(
                name: "metadata_record_observations");

            migrationBuilder.DropColumn(
                name: "RefreshCadenceDays",
                table: "metadata_records");

            migrationBuilder.DropColumn(
                name: "ScanHour",
                table: "libraries");

            migrationBuilder.DropColumn(
                name: "ScanWeekday",
                table: "libraries");

            migrationBuilder.DropColumn(
                name: "BackupHour",
                table: "app_settings");

            migrationBuilder.DropColumn(
                name: "CacheEvictionHour",
                table: "app_settings");

            migrationBuilder.DropColumn(
                name: "MetadataRefreshFinishedDays",
                table: "app_settings");

            migrationBuilder.DropColumn(
                name: "MetadataRefreshFollowPace",
                table: "app_settings");

            migrationBuilder.DropColumn(
                name: "MetadataRefreshHour",
                table: "app_settings");

            migrationBuilder.DropColumn(
                name: "MetadataRefreshOngoingDays",
                table: "app_settings");
        }
    }
}
