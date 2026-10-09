using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TailoredApps.KickGateway.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLiveTranscripts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LiveTranscripts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DedupeKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ChannelSlug = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    KickChannelId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AudioStartSeconds = table.Column<double>(type: "float", nullable: false),
                    AudioSeconds = table.Column<double>(type: "float", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Language = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Confidence = table.Column<float>(type: "real", nullable: false),
                    SegmentsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SegmentCount = table.Column<int>(type: "int", nullable: false),
                    FirstMediaSequence = table.Column<long>(type: "bigint", nullable: false),
                    LastMediaSequence = table.Column<long>(type: "bigint", nullable: false),
                    Model = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TranscribedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ProcessingSeconds = table.Column<double>(type: "float", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LiveTranscripts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LiveTranscripts_ChannelSlug_EndedAt",
                table: "LiveTranscripts",
                columns: new[] { "ChannelSlug", "EndedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_LiveTranscripts_ChannelSlug_StartedAt",
                table: "LiveTranscripts",
                columns: new[] { "ChannelSlug", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_LiveTranscripts_DedupeKey",
                table: "LiveTranscripts",
                column: "DedupeKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LiveTranscripts");
        }
    }
}
