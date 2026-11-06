using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TailoredApps.KickGateway.Api.Data.Migrations
{
    /// <summary>
    /// Puts <c>Platform</c> into the analytics indexes: every channel/chatter query filters on it, and without it each
    /// row in a window cost a key lookup (or the aggregates scanned the whole table).
    /// <para>
    /// Idempotent on purpose. On prod these indexes are built by hand first
    /// (<c>deploy/sql/20261106_platform_indexes.sql</c>, ONLINE where the edition allows), because building them here
    /// runs inside Api startup and on a large <c>ChatMessages</c> table takes far longer than a startup should. This
    /// migration then finds every index in place and only records itself; on a fresh or small database (dev, tests)
    /// it builds them. Names are EF's defaults, so the model snapshot matches what the script creates.
    /// </para>
    /// </summary>
    public partial class AddPlatformToAnalyticsIndexes : Migration
    {
        private sealed record IndexDef(string Table, string Name, string Columns);

        // Smallest tables first, so a partial run already helps the most frequent query (transcript coverage).
        private static readonly IndexDef[] Added =
        {
            new("LiveTranscripts", "IX_LiveTranscripts_ChannelSlug_Platform_StartedAt",
                "([ChannelSlug], [Platform], [StartedAt]) INCLUDE ([AudioSeconds], [Language], [Confidence])"),
            new("LiveTranscripts", "IX_LiveTranscripts_ChannelSlug_Platform_EndedAt",
                "([ChannelSlug], [Platform], [EndedAt])"),
            new("ChatterEvents", "IX_ChatterEvents_ChannelSlug_Platform_OccurredAt",
                "([ChannelSlug], [Platform], [OccurredAt]) INCLUDE ([Kind], [Amount], [RefId])"),
            new("ChatterEvents", "IX_ChatterEvents_UserId_Platform_OccurredAt",
                "([UserId], [Platform], [OccurredAt])"),
            new("ChatterEvents", "IX_ChatterEvents_CounterpartUserId_Platform_OccurredAt",
                "([CounterpartUserId], [Platform], [OccurredAt])"),
            new("ChatterEvents", "IX_ChatterEvents_RefId_Kind",
                "([RefId], [Kind]) INCLUDE ([Platform])"),
            new("ChatMessages", "IX_ChatMessages_ChannelSlug_Platform_CreatedAt",
                "([ChannelSlug], [Platform], [CreatedAt]) INCLUDE ([SenderUserId], [ReplyToMessageId], [ReplyToUserId])"),
            new("ChatMessages", "IX_ChatMessages_ChannelSlug_Platform_SenderUserId_CreatedAt",
                "([ChannelSlug], [Platform], [SenderUserId], [CreatedAt])"),
            new("ChatMessages", "IX_ChatMessages_SenderUsername_Platform_CreatedAt",
                "([SenderUsername], [Platform], [CreatedAt]) INCLUDE ([SenderUserId])"),
            new("ChatMessages", "IX_ChatMessages_SenderUserId_Platform_CreatedAt",
                "([SenderUserId], [Platform], [CreatedAt]) INCLUDE ([SenderUsername], [ChannelSlug])"),
            new("ChatMessages", "IX_ChatMessages_ReplyToUserId_Platform_CreatedAt",
                "([ReplyToUserId], [Platform], [CreatedAt])"),
            new("ChatMessages", "IX_ChatMessages_CreatedAt",
                "([CreatedAt]) INCLUDE ([Platform], [ChannelSlug], [SenderUserId])"),
        };

        // What the indexes above replace (the pre-Platform definitions, needed again by Down).
        private static readonly IndexDef[] Replaced =
        {
            new("LiveTranscripts", "IX_LiveTranscripts_ChannelSlug_StartedAt", "([ChannelSlug], [StartedAt])"),
            new("LiveTranscripts", "IX_LiveTranscripts_ChannelSlug_EndedAt", "([ChannelSlug], [EndedAt])"),
            new("ChatterEvents", "IX_ChatterEvents_ChannelSlug_OccurredAt", "([ChannelSlug], [OccurredAt])"),
            new("ChatterEvents", "IX_ChatterEvents_UserId_OccurredAt", "([UserId], [OccurredAt])"),
            new("ChatterEvents", "IX_ChatterEvents_CounterpartUserId_OccurredAt", "([CounterpartUserId], [OccurredAt])"),
            new("ChatterEvents", "IX_ChatterEvents_RefId", "([RefId])"),
            new("ChatMessages", "IX_ChatMessages_ChannelSlug_CreatedAt", "([ChannelSlug], [CreatedAt])"),
            new("ChatMessages", "IX_ChatMessages_ChannelSlug_SenderUserId_CreatedAt", "([ChannelSlug], [SenderUserId], [CreatedAt])"),
            new("ChatMessages", "IX_ChatMessages_SenderUsername_CreatedAt", "([SenderUsername], [CreatedAt]) INCLUDE ([SenderUserId])"),
            new("ChatMessages", "IX_ChatMessages_SenderUserId_CreatedAt", "([SenderUserId], [CreatedAt]) INCLUDE ([SenderUsername])"),
            new("ChatMessages", "IX_ChatMessages_ReplyToUserId_CreatedAt", "([ReplyToUserId], [CreatedAt])"),
        };

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // New first, old after: the queries always have an index to use.
            foreach (var ix in Added) migrationBuilder.Sql(Create(ix));
            foreach (var ix in Replaced) migrationBuilder.Sql(Drop(ix));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var ix in Replaced) migrationBuilder.Sql(Create(ix));
            foreach (var ix in Added) migrationBuilder.Sql(Drop(ix));
        }

        private static string Exists(IndexDef ix) =>
            $"EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'[{ix.Table}]') AND name = N'{ix.Name}')";

        private static string Create(IndexDef ix) =>
            $"IF NOT {Exists(ix)} CREATE INDEX [{ix.Name}] ON [{ix.Table}] {ix.Columns};";

        private static string Drop(IndexDef ix) =>
            $"IF {Exists(ix)} DROP INDEX [{ix.Name}] ON [{ix.Table}];";
    }
}
