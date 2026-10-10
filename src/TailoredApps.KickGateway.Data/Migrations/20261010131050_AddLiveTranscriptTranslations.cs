using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TailoredApps.KickGateway.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLiveTranscriptTranslations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LiveTranscriptTranslations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TranscriptDedupeKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ChannelSlug = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AudioStartSeconds = table.Column<double>(type: "float", nullable: false),
                    SourceLanguage = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    TargetLanguage = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Text = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    SegmentsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SegmentCount = table.Column<int>(type: "int", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    TranslatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ProcessingSeconds = table.Column<double>(type: "float", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LiveTranscriptTranslations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LiveTranscriptTranslations_ChannelSlug_StartedAt",
                table: "LiveTranscriptTranslations",
                columns: new[] { "ChannelSlug", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_LiveTranscriptTranslations_TranscriptDedupeKey_TargetLanguage",
                table: "LiveTranscriptTranslations",
                columns: new[] { "TranscriptDedupeKey", "TargetLanguage" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LiveTranscriptTranslations");
        }
    }
}
