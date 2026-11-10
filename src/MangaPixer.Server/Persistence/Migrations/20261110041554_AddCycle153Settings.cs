using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace com.lifepixer.mangapixer.Server.Persistence.Migrations
{
    /// <summary>
    /// 1.53.0 step 0 (migration #61), additive: five columns for the cycle's lanes. <c>reader_preferences.ShareRatings</c> and
    /// <c>reader_preferences.ShowRecommendationsHomeRow</c> are non-null with default TRUE, so every existing preferences row reads as ON
    /// (owner decisions 3b and 4; new rows get true from the entity initializers, like <c>SeriesInfoOnHover</c>);
    /// <c>app_settings.RatingSharingEnabled</c> and <c>tracker_accounts.SyncScores</c> are non-null with default false (off until an
    /// admin / the user turns them on); <c>users.LastSeenVersion</c> is nullable (max 64) - null for every existing user.
    /// </summary>
    public partial class AddCycle153Settings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastSeenVersion",
                table: "users",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SyncScores",
                table: "tracker_accounts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ShareRatings",
                table: "reader_preferences",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "ShowRecommendationsHomeRow",
                table: "reader_preferences",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "RatingSharingEnabled",
                table: "app_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastSeenVersion",
                table: "users");

            migrationBuilder.DropColumn(
                name: "SyncScores",
                table: "tracker_accounts");

            migrationBuilder.DropColumn(
                name: "ShareRatings",
                table: "reader_preferences");

            migrationBuilder.DropColumn(
                name: "ShowRecommendationsHomeRow",
                table: "reader_preferences");

            migrationBuilder.DropColumn(
                name: "RatingSharingEnabled",
                table: "app_settings");
        }
    }
}
