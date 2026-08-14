using System.Text.Json;
using TailoredApps.KickGateway.Contracts.Realtime;
using TailoredApps.KickGateway.Realtime;
using TailoredApps.KickGateway.Realtime.Mapping;
using TailoredApps.KickGateway.Realtime.Pusher;
using Xunit;

namespace TailoredApps.KickGateway.Tests;

public class RealtimeFrameMapperTests
{
    private static readonly RealtimeFrameMapper Mapper = new();
    private static readonly ManagedChannel Channel = new("xqc", "668", "12345", VideoCaptureEnabled: true);
    private static readonly DateTime Now = new(2026, 7, 23, 12, 0, 0, DateTimeKind.Utc);

    private static IKickRealtimeEvent Map(string shortEvent, object data, string? channel = "chatrooms.12345.v2")
    {
        var frame = new PusherFrame($"App\\Events\\{shortEvent}", channel, JsonSerializer.Serialize(data));
        return Mapper.Map(frame, Channel, Now);
    }

    [Fact]
    public void Maps_chat_message_with_envelope()
    {
        var evt = Map("ChatMessageEvent", new
        {
            id = "msg-uuid-1",
            content = "hello chat",
            type = "message",
            created_at = "2026-07-23T11:59:00Z",
            sender = new
            {
                id = 676,
                username = "viewer",
                slug = "viewer",
                identity = new { color = "#e9113c", badges = new[] { new { type = "subscriber", text = "Sub", count = 6 } } },
            },
        });

        var chat = Assert.IsType<RealtimeChatMessage>(evt);
        Assert.Equal("msg-uuid-1", chat.MessageId);
        Assert.Equal("hello chat", chat.Content);
        Assert.Equal("message", chat.MessageType);
        Assert.Equal("676", chat.SenderId);
        Assert.Equal("viewer", chat.SenderUsername);
        Assert.Equal("#e9113c", chat.SenderColor);
        Assert.Single(chat.SenderBadges);
        Assert.Equal("subscriber", chat.SenderBadges[0].Type);
        Assert.Equal(6, chat.SenderBadges[0].Count);

        // envelope
        Assert.Equal("xqc", chat.BroadcasterSlug);
        Assert.Equal("668", chat.KickChannelId);
        Assert.Equal("12345", chat.KickChatroomId);
        Assert.Equal("chatrooms.12345.v2", chat.PusherChannel);
        Assert.Equal("App\\Events\\ChatMessageEvent", chat.PusherEvent);
        Assert.Contains("msg-uuid-1", chat.DedupeKey);   // natural id → dedupe key
        Assert.Contains("hello chat", chat.RawData);      // inner JSON preserved
    }

    [Fact]
    public void Maps_chat_reply_metadata()
    {
        var evt = Map("ChatMessageEvent", new
        {
            id = "reply-1",
            content = "@og yes",
            type = "reply",
            sender = new { id = 1, username = "a", slug = "a" },
            metadata = new
            {
                original_message = new { id = "orig-9", content = "og text" },
                original_sender = new { id = 2, username = "og" },
            },
        });

        var chat = Assert.IsType<RealtimeChatMessage>(evt);
        Assert.Equal("orig-9", chat.ReplyToMessageId);
        Assert.Equal("og", chat.ReplyToSenderUsername);
    }

    [Fact]
    public void Maps_timeout_as_non_permanent_ban()
    {
        var evt = Map("UserBannedEvent", new
        {
            id = "ban-1",
            user = new { id = 5, username = "bad", slug = "bad" },
            banned_by = new { id = 9, username = "mod" },
            permanent = false,
            expires_at = "2026-07-23T13:00:00Z",
        });

        var ban = Assert.IsType<RealtimeUserBanned>(evt);
        Assert.False(ban.Permanent);
        Assert.NotNull(ban.ExpiresAt);
        Assert.Equal("bad", ban.BannedUsername);
        Assert.Equal("mod", ban.ModeratorUsername);
    }

    [Fact]
    public void Maps_permanent_ban()
    {
        var evt = Map("UserBannedEvent", new
        {
            id = "ban-2",
            user = new { id = 5, username = "bad", slug = "bad" },
            banned_by = new { id = 9, username = "mod" },
            permanent = true,
        });

        var ban = Assert.IsType<RealtimeUserBanned>(evt);
        Assert.True(ban.Permanent);
        Assert.Null(ban.ExpiresAt);
    }

    [Fact]
    public void Maps_gifted_subscriptions_count()
    {
        var evt = Map("GiftedSubscriptionsEvent", new
        {
            chatroom_id = 12345,
            gifted_usernames = new[] { "a", "b", "c" },
            gifter_username = "santa",
            gifter_total = 42,
        }, channel: "channel.668");

        var gift = Assert.IsType<RealtimeGiftedSubscriptions>(evt);
        Assert.Equal("santa", gift.GifterUsername);
        Assert.Equal(3, gift.Count);
        Assert.Equal(new[] { "a", "b", "c" }, gift.GiftedUsernames);
        Assert.Equal(42, gift.GifterTotal);
    }

    [Fact]
    public void Maps_followers_updated_camelcase()
    {
        var evt = Map("FollowersUpdated", new { followersCount = 999, channel_id = 668 }, channel: "channel.668");
        var f = Assert.IsType<RealtimeFollowersUpdated>(evt);
        Assert.Equal(999, f.FollowersCount);
    }

    [Fact]
    public void Maps_chatroom_modes()
    {
        var evt = Map("ChatroomUpdatedEvent", new
        {
            id = 12345,
            slow_mode = new { enabled = true, message_interval = 6 },
            followers_mode = new { enabled = true, min_duration = 10 },
            subscribers_mode = new { enabled = false },
            emotes_mode = new { enabled = true },
        });

        var c = Assert.IsType<RealtimeChatroomUpdated>(evt);
        Assert.True(c.SlowModeEnabled);
        Assert.Equal(6, c.SlowModeIntervalSeconds);
        Assert.True(c.FollowersOnly);
        Assert.Equal(10, c.FollowersOnlyMinMinutes);
        Assert.False(c.SubscribersOnly);
        Assert.True(c.EmotesOnly);
    }

    [Fact]
    public void Unknown_event_falls_through_to_unknown()
    {
        var evt = Map("SomeBrandNewEvent", new { whatever = 1 });
        var unknown = Assert.IsType<KickRealtimeUnknown>(evt);
        Assert.Equal("App\\Events\\SomeBrandNewEvent", unknown.PusherEvent);
        Assert.Contains("whatever", unknown.RawData);
    }

    [Fact]
    public void Invalid_json_does_not_throw_and_maps_unknown()
    {
        var frame = new PusherFrame("App\\Events\\ChatMessageEvent", "chatrooms.12345.v2", "{ not valid json");
        var evt = Mapper.Map(frame, Channel, Now);
        Assert.IsType<KickRealtimeUnknown>(evt);
        Assert.Equal("{ not valid json", evt.RawData);
    }
}
