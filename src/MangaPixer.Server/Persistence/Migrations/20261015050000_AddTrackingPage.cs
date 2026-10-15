using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace com.lifepixer.mangapixer.Server.Persistence.Migrations
{
    /// <summary>
    /// 1.43.0 (lane G, Tracking page): the columns the Tracking redesign added to <c>tracker_entries</c> (MangaPixer's whole chapter /
    /// volume totals, the tracker's totals and dates, the automatic dates, the user's date overrides, "dates pending") and
    /// <c>tracker_settings.DailyRequestCap</c> (null = the default 2000). A migration of its own - not an amendment of
    /// <c>AddTrackerSync</c> - because a review instance had already applied that one (an applied migration never re-runs).
    /// All nullable except <c>DatesPending</c> (NOT NULL, default false: no dates waiting).
    /// </summary>
    public partial class AddTrackingPage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AllChapters",
                table: "tracker_entries",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AllVolumes",
                table: "tracker_entries",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RemoteTotalChapters",
                table: "tracker_entries",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RemoteTotalVolumes",
                table: "tracker_entries",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AutoStartedOn",
                table: "tracker_entries",
                type: "TEXT",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AutoFinishedOn",
                table: "tracker_entries",
                type: "TEXT",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StartedOnOverride",
                table: "tracker_entries",
                type: "TEXT",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FinishedOnOverride",
                table: "tracker_entries",
                type: "TEXT",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DatesPending",
                table: "tracker_entries",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "RemoteStartedOn",
                table: "tracker_entries",
                type: "TEXT",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RemoteFinishedOn",
                table: "tracker_entries",
                type: "TEXT",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DailyRequestCap",
                table: "tracker_settings",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllChapters",
                table: "tracker_entries");

            migrationBuilder.DropColumn(
                name: "AllVolumes",
                table: "tracker_entries");

            migrationBuilder.DropColumn(
                name: "RemoteTotalChapters",
                table: "tracker_entries");

            migrationBuilder.DropColumn(
                name: "RemoteTotalVolumes",
                table: "tracker_entries");

            migrationBuilder.DropColumn(
                name: "AutoStartedOn",
                table: "tracker_entries");

            migrationBuilder.DropColumn(
                name: "AutoFinishedOn",
                table: "tracker_entries");

            migrationBuilder.DropColumn(
                name: "StartedOnOverride",
                table: "tracker_entries");

            migrationBuilder.DropColumn(
                name: "FinishedOnOverride",
                table: "tracker_entries");

            migrationBuilder.DropColumn(
                name: "DatesPending",
                table: "tracker_entries");

            migrationBuilder.DropColumn(
                name: "RemoteStartedOn",
                table: "tracker_entries");

            migrationBuilder.DropColumn(
                name: "RemoteFinishedOn",
                table: "tracker_entries");

            migrationBuilder.DropColumn(
                name: "DailyRequestCap",
                table: "tracker_settings");
        }
    }
}
