using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TailoredApps.KickGateway.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTwitchGateway : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Platform",
                table: "LiveTranscriptTranslations",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "kick");

            migrationBuilder.AddColumn<string>(
                name: "Platform",
                table: "LiveTranscripts",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "kick");

            migrationBuilder.AddColumn<string>(
                name: "Platform",
                table: "ChatterEvents",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "kick");

            migrationBuilder.AddColumn<string>(
                name: "Platform",
                table: "ChatMessages",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "kick");

            migrationBuilder.CreateTable(
                name: "ReceivedTwitchEvents",
                columns: table => new
                {
                    DedupeKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    EventVersion = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    BroadcasterUserId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    BroadcasterLogin = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TwitchTimestamp = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PublishedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RawData = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceivedTwitchEvents", x => x.DedupeKey);
                });

            migrationBuilder.CreateTable(
                name: "TwitchBotAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Login = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    AccessToken = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    RefreshToken = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Scopes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    AccessTokenExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastRefreshedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastValidatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TwitchBotAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TwitchChannels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Login = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    DisplayName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CaptureChat = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CaptureAudio = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    LastResolvedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TwitchChannels", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReceivedTwitchEvents_BroadcasterLogin",
                table: "ReceivedTwitchEvents",
                column: "BroadcasterLogin");

            migrationBuilder.CreateIndex(
                name: "IX_ReceivedTwitchEvents_ReceivedAt",
                table: "ReceivedTwitchEvents",
                column: "ReceivedAt");

            migrationBuilder.CreateIndex(
                name: "IX_TwitchBotAccounts_UserId",
                table: "TwitchBotAccounts",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TwitchChannels_Login",
                table: "TwitchChannels",
                column: "Login",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TwitchChannels_UserId",
                table: "TwitchChannels",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReceivedTwitchEvents");

            migrationBuilder.DropTable(
                name: "TwitchBotAccounts");

            migrationBuilder.DropTable(
                name: "TwitchChannels");

            migrationBuilder.DropColumn(
                name: "Platform",
                table: "LiveTranscriptTranslations");

            migrationBuilder.DropColumn(
                name: "Platform",
                table: "LiveTranscripts");

            migrationBuilder.DropColumn(
                name: "Platform",
                table: "ChatterEvents");

            migrationBuilder.DropColumn(
                name: "Platform",
                table: "ChatMessages");
        }
    }
}
