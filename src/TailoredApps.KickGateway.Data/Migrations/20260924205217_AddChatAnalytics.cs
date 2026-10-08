using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TailoredApps.KickGateway.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChatAnalytics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AnalyticsCheckpoints",
                columns: table => new
                {
                    Name = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Position = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PositionKey = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    ProcessedCount = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnalyticsCheckpoints", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "ChatMessages",
                columns: table => new
                {
                    MessageId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    SourceId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    BroadcasterAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BroadcasterUserId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ChannelSlug = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    SenderUserId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SenderUsername = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    SenderChannelSlug = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    SenderIsVerified = table.Column<bool>(type: "bit", nullable: false),
                    SenderColor = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    SenderBadges = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Content = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReplyToMessageId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ReplyToUserId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ReplyToUsername = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatMessages", x => x.MessageId);
                });

            migrationBuilder.CreateTable(
                name: "ChatterEvents",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    SourceId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    BroadcasterAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ChannelSlug = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Username = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    CounterpartUserId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CounterpartUsername = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    Amount = table.Column<int>(type: "int", nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RefId = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatterEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChatMentions",
                columns: table => new
                {
                    MessageId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    MentionedUsername = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatMentions", x => new { x.MessageId, x.MentionedUsername });
                    table.ForeignKey(
                        name: "FK_ChatMentions_ChatMessages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "ChatMessages",
                        principalColumn: "MessageId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChatMentions_MentionedUsername",
                table: "ChatMentions",
                column: "MentionedUsername");

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessages_ChannelSlug_CreatedAt",
                table: "ChatMessages",
                columns: new[] { "ChannelSlug", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessages_ChannelSlug_SenderUserId_CreatedAt",
                table: "ChatMessages",
                columns: new[] { "ChannelSlug", "SenderUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessages_ReplyToMessageId",
                table: "ChatMessages",
                column: "ReplyToMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessages_ReplyToUserId_CreatedAt",
                table: "ChatMessages",
                columns: new[] { "ReplyToUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessages_SenderUserId_CreatedAt",
                table: "ChatMessages",
                columns: new[] { "SenderUserId", "CreatedAt" })
                .Annotation("SqlServer:Include", new[] { "SenderUsername" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessages_SenderUsername_CreatedAt",
                table: "ChatMessages",
                columns: new[] { "SenderUsername", "CreatedAt" })
                .Annotation("SqlServer:Include", new[] { "SenderUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatterEvents_ChannelSlug_OccurredAt",
                table: "ChatterEvents",
                columns: new[] { "ChannelSlug", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatterEvents_CounterpartUserId_OccurredAt",
                table: "ChatterEvents",
                columns: new[] { "CounterpartUserId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatterEvents_RefId",
                table: "ChatterEvents",
                column: "RefId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatterEvents_UserId_OccurredAt",
                table: "ChatterEvents",
                columns: new[] { "UserId", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnalyticsCheckpoints");

            migrationBuilder.DropTable(
                name: "ChatMentions");

            migrationBuilder.DropTable(
                name: "ChatterEvents");

            migrationBuilder.DropTable(
                name: "ChatMessages");
        }
    }
}
