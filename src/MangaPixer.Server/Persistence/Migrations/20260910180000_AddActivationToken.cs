using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace com.lifepixer.mangaplex.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddActivationToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPendingActivation",
                table: "users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ActivationTokenHash",
                table: "users",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ActivationTokenExpiry",
                table: "users",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ActivationTokenConsumed",
                table: "users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsPendingActivation",
                table: "users");

            migrationBuilder.DropColumn(
                name: "ActivationTokenHash",
                table: "users");

            migrationBuilder.DropColumn(
                name: "ActivationTokenExpiry",
                table: "users");

            migrationBuilder.DropColumn(
                name: "ActivationTokenConsumed",
                table: "users");
        }
    }
}
