using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TailoredApps.KickGateway.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRealtimeIngest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "VideoCaptureEnabled",
                table: "Broadcasters",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "ReceivedRealtimeEvents",
                columns: table => new
                {
                    DedupeKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    EventName = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    PusherChannel = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    ChannelId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ChatroomId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ReceivedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PublishedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RawData = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceivedRealtimeEvents", x => x.DedupeKey);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReceivedRealtimeEvents_ReceivedAt",
                table: "ReceivedRealtimeEvents",
                column: "ReceivedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ReceivedRealtimeEvents_Slug",
                table: "ReceivedRealtimeEvents",
                column: "Slug");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReceivedRealtimeEvents");

            migrationBuilder.DropColumn(
                name: "VideoCaptureEnabled",
                table: "Broadcasters");
        }
    }
}
