using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TailoredApps.KickGateway.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRealtimeChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RealtimeChannels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CaptureChat = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CaptureChannel = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    VideoCaptureEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    ChannelId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ChatroomId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    LastResolvedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RealtimeChannels", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RealtimeChannels_Slug",
                table: "RealtimeChannels",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RealtimeChannels");
        }
    }
}
