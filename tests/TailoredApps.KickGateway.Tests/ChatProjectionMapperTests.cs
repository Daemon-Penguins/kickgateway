using TailoredApps.Integrations.Kick;
using TailoredApps.KickGateway.Api.Analytics;
using TailoredApps.KickGateway.Api.Data;
using Xunit;

namespace TailoredApps.KickGateway.Tests;

public class ChatProjectionMapperTests
{
    private static readonly DateTime Received = new(2026, 9, 1, 18, 0, 5, DateTimeKind.Utc);

    private static ReceivedWebhook Row(string type, string body, Guid? account = null) => new()
    {
        MessageId = "wh-1",
        EventType = type,
        BroadcasterAccountId = account,
        ReceivedAt = Received,
        RawBody = body,
    };

    // Shape per docs.kick.com/events/event-types (chat.message.sent v1).
    private const string ChatReply = """
    {
      "message_id": "msg-2",
      "replies_to": {
        "message_id": "msg-1",
        "content": "hello",
        "sender": { "is_anonymous": false, "user_id": 111, "username": "Alice", "channel_slug": "alice", "identity": null }
      },
      "broadcaster": { "user_id": 1000, "username": "Streamer", "channel_slug": "Streamer_Chan" },
      "sender": {
        "is_anonymous": false, "user_id": 222, "username": "Bob", "is_verified": true, "channel_slug": "bob",
        "identity": { "username_color": "#FF5733",
          "badges": [ { "text": "Moderator", "type": "moderator" }, { "text": "Subscriber", "type": "subscriber", "count": 14 } ] }
      },
      "content": "hey @Alice and @carol_99 [emote:37226:KEKW] mail me a@b.com @alice",
      "emotes": [ { "emote_id": "37226", "positions": [ { "s": 26, "e": 43 } ] } ],
      "created_at": "2026-09-01T18:00:03Z"
    }
    """;

    [Fact]
    public void Chat_message_maps_sender_reply_badges_and_mentions()
    {
        var p = ChatProjectionMapper.Map(Row(KickEventTypes.ChatMessageSent, ChatReply), knownSlug: null);

        var m = Assert.Single(p.Messages);
        Assert.Empty(p.Events);
        Assert.Equal("msg-2", m.MessageId);
        Assert.Equal(ChatSource.Webhook, m.Source);
        Assert.Equal("wh-1", m.SourceId);
        Assert.Equal("streamer_chan", m.ChannelSlug);
        Assert.Equal("1000", m.BroadcasterUserId);
        Assert.Equal("222", m.SenderUserId);
        Assert.Equal("Bob", m.SenderUsername);
        Assert.True(m.SenderIsVerified);
        Assert.Equal("#FF5733", m.SenderColor);
        Assert.Equal("moderator:0|subscriber:14", m.SenderBadges);
        Assert.Equal(new DateTime(2026, 9, 1, 18, 0, 3, DateTimeKind.Utc), m.CreatedAt);
        Assert.Equal("msg-1", m.ReplyToMessageId);
        Assert.Equal("111", m.ReplyToUserId);
        Assert.Equal("Alice", m.ReplyToUsername);
        // Deduped + lowercased; the e-mail address is not a mention.
        Assert.Equal(new[] { "alice", "carol_99" }, m.Mentions.Select(x => x.MentionedUsername));
    }

    [Fact]
    public void Chat_without_payload_slug_falls_back_to_known_slug_and_receive_time()
    {
        const string body = """{ "message_id": "m", "sender": { "user_id": 5, "username": "x" }, "content": "hi" }""";
        var m = Assert.Single(ChatProjectionMapper.Map(Row(KickEventTypes.ChatMessageSent, body), "Known").Messages);
        Assert.Equal("known", m.ChannelSlug);
        Assert.Equal(Received, m.CreatedAt);
        Assert.Null(m.ReplyToMessageId);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{ "message_id": "m", "content": "no sender", "broadcaster": { "channel_slug": "c" } }""")]
    public void Unusable_payloads_yield_nothing(string body)
    {
        var p = ChatProjectionMapper.Map(Row(KickEventTypes.ChatMessageSent, body), "c");
        Assert.Empty(p.Messages);
        Assert.Empty(p.Events);
    }

    [Fact]
    public void Unprojected_event_types_yield_nothing()
    {
        const string body = """{ "broadcaster": { "channel_slug": "c" }, "is_live": true }""";
        Assert.Same(ChatProjection.Empty, ChatProjectionMapper.Map(Row(KickEventTypes.LivestreamStatusUpdated, body), null));
    }

    [Fact]
    public void Gifts_emit_one_row_per_giftee()
    {
        const string body = """
        {
          "broadcaster": { "user_id": 1000, "channel_slug": "chan" },
          "gifter": { "is_anonymous": false, "user_id": 7, "username": "Gifter" },
          "giftees": [ { "user_id": 8, "username": "One" }, { "user_id": 9, "username": "Two" } ],
          "created_at": "2026-09-01T18:00:00Z"
        }
        """;
        var events = ChatProjectionMapper.Map(Row(KickEventTypes.SubscriptionGifts, body), null).Events;

        Assert.Equal(2, events.Count);
        Assert.All(events, e =>
        {
            Assert.Equal(ChatterEventKind.SubscriptionGift, e.Kind);
            Assert.Equal("7", e.UserId);
            Assert.Equal(1, e.Amount);
        });
        Assert.Equal(new[] { "8", "9" }, events.Select(e => e.CounterpartUserId));
        Assert.Equal(new[] { "wh-1:gift:0", "wh-1:gift:1" }, events.Select(e => e.Id));
        Assert.All(events, e => Assert.Equal(Received, e.OccurredAt));
    }

    [Fact]
    public void Anonymous_gift_without_giftees_keeps_the_count()
    {
        const string body = """{ "broadcaster": { "channel_slug": "chan" }, "gifter": { "is_anonymous": true, "user_id": null }, "gift_count": 5 }""";
        var e = Assert.Single(ChatProjectionMapper.Map(Row(KickEventTypes.SubscriptionGifts, body), null).Events);
        Assert.Equal("", e.UserId);
        Assert.Equal(5, e.Amount);
        Assert.Null(e.CounterpartUserId);
    }

    [Fact]
    public void Kicks_read_sender_and_gift_object()
    {
        const string body = """
        { "broadcaster": { "channel_slug": "chan" }, "sender": { "user_id": 3, "username": "Rich" },
          "gift": { "amount": 500, "name": "Rage Quit", "message": "gg" }, "created_at": "2026-09-01T18:00:00Z" }
        """;
        var e = Assert.Single(ChatProjectionMapper.Map(Row(KickEventTypes.KicksGifted, body), null).Events);
        Assert.Equal(ChatterEventKind.KicksGift, e.Kind);
        Assert.Equal("3", e.UserId);
        Assert.Equal(500, e.Amount);
        Assert.Equal("gg", e.Detail);
    }

    [Fact]
    public void Reward_redemption_reads_redeemer_and_id()
    {
        const string body = """
        { "id": "RDM1", "status": "accepted", "redeemed_at": "2026-09-01T18:00:00Z",
          "reward": { "id": "r", "title": "Hydrate", "cost": 100 },
          "redeemer": { "user_id": 4, "username": "Thirsty" }, "broadcaster": { "channel_slug": "chan" } }
        """;
        var e = Assert.Single(ChatProjectionMapper.Map(Row(KickEventTypes.ChannelRewardRedemptionUpdated, body), null).Events);
        Assert.Equal(ChatterEventKind.RewardRedemption, e.Kind);
        Assert.Equal("4", e.UserId);
        Assert.Equal("Hydrate", e.Detail);
        Assert.Equal("RDM1", e.RefId);
        Assert.Equal(100, e.Amount);
    }

    [Theory]
    [InlineData("\"2026-09-01T18:10:00Z\"", ChatterEventKind.Timeout)]
    [InlineData("null", ChatterEventKind.Ban)]
    public void Ban_vs_timeout_follows_expiry(string expires, ChatterEventKind expected)
    {
        var body = $$"""
        { "broadcaster": { "channel_slug": "chan" },
          "moderator": { "user_id": 2, "username": "Mod" },
          "banned_user": { "user_id": 9, "username": "Troll" },
          "metadata": { "reason": "spam", "created_at": "2026-09-01T18:00:00Z", "expires_at": {{expires}} } }
        """;
        var e = Assert.Single(ChatProjectionMapper.Map(Row(KickEventTypes.ModerationBanned, body), null).Events);
        Assert.Equal(expected, e.Kind);
        Assert.Equal("9", e.UserId);
        Assert.Equal("2", e.CounterpartUserId);
        Assert.Equal("spam", e.Detail);
    }

    [Fact]
    public void Follow_and_subscription_map_actor()
    {
        const string follow = """{ "broadcaster": { "channel_slug": "chan" }, "follower": { "user_id": 12, "username": "Fan" } }""";
        var f = Assert.Single(ChatProjectionMapper.Map(Row(KickEventTypes.ChannelFollowed, follow), null).Events);
        Assert.Equal(ChatterEventKind.Follow, f.Kind);
        Assert.Equal("12", f.UserId);

        // created_at on subscription payloads is the subscription's start (repeated on every renewal) —
        // the event time must be the delivery time instead.
        const string sub = """
        { "broadcaster": { "channel_slug": "chan" }, "subscriber": { "user_id": 13, "username": "Sub" }, "duration": 3,
          "created_at": "2025-04-24T18:57:50Z", "expires_at": "2026-10-24T18:57:50Z" }
        """;
        var s = Assert.Single(ChatProjectionMapper.Map(Row(KickEventTypes.SubscriptionRenewal, sub), null).Events);
        Assert.Equal(ChatterEventKind.SubscriptionRenewal, s.Kind);
        Assert.Equal(3, s.Amount);
        Assert.Equal(Received, s.OccurredAt);
        Assert.Equal(new DateTime(2026, 10, 24, 18, 57, 50, DateTimeKind.Utc), s.ExpiresAt);
    }

    [Theory]
    [InlineData("@bob hi", "me", new[] { "bob" })]
    [InlineData("hi @me and @Bob @bob", "Me", new[] { "bob" })]
    [InlineData("x@y.com", null, new string[0])]
    [InlineData("@a", null, new string[0])]
    public void Extract_mentions(string content, string? sender, string[] expected) =>
        Assert.Equal(expected, ChatProjectionMapper.ExtractMentions(content, sender));
}

public class RealtimeChatProjectionMapperTests
{
    private static ReceivedRealtimeEvent Row(string evt, string data, string slug = "Beta") => new()
    {
        DedupeKey = $"chatrooms.5.v2|{evt}|k",
        EventName = $"App\\Events\\{evt}",
        PusherChannel = "chatrooms.5.v2",
        Slug = slug,
        ReceivedAt = new DateTime(2026, 9, 1, 18, 0, 0, DateTimeKind.Utc),
        RawData = data,
    };

    [Fact]
    public void Chat_reply_maps_from_pusher_shape()
    {
        const string data = """
        { "id": "uuid-2", "chatroom_id": 5, "content": "yo @gina", "type": "reply", "created_at": "2026-09-01T18:00:01+00:00",
          "sender": { "id": 6, "username": "Frank", "slug": "frank",
            "identity": { "color": "#00FF00", "badges": [ { "type": "vip", "text": "VIP" } ] } },
          "metadata": { "original_sender": { "id": "7", "username": "Gina" }, "original_message": { "id": "uuid-1", "content": "hi" } } }
        """;
        var m = Assert.Single(RealtimeChatProjectionMapper.Map(Row("ChatMessageEvent", data), webhookCoversChannel: true).Messages);
        Assert.Equal("uuid-2", m.MessageId);
        Assert.Equal(ChatSource.Realtime, m.Source);
        Assert.Equal("beta", m.ChannelSlug);
        Assert.Equal("6", m.SenderUserId);
        Assert.Equal("#00FF00", m.SenderColor);
        Assert.Equal("vip:0", m.SenderBadges);
        Assert.Equal("uuid-1", m.ReplyToMessageId);
        Assert.Equal("7", m.ReplyToUserId);
        Assert.Equal("gina", Assert.Single(m.Mentions).MentionedUsername);
    }

    [Fact]
    public void Webhook_covered_channel_keeps_only_realtime_only_kinds()
    {
        const string ban = """{ "user": { "id": 9, "username": "Troll" }, "banned_by": { "id": 2, "username": "Mod" }, "permanent": true }""";
        Assert.Empty(RealtimeChatProjectionMapper.Map(Row("UserBannedEvent", ban), webhookCoversChannel: true).Events);
        Assert.Equal(ChatterEventKind.Ban,
            Assert.Single(RealtimeChatProjectionMapper.Map(Row("UserBannedEvent", ban), webhookCoversChannel: false).Events).Kind);

        const string deleted = """{ "id": "evt", "message": { "id": "uuid-9" }, "aiModerated": true }""";
        var d = Assert.Single(RealtimeChatProjectionMapper.Map(Row("MessageDeletedEvent", deleted), webhookCoversChannel: true).Events);
        Assert.Equal(ChatterEventKind.MessageDeleted, d.Kind);
        Assert.Equal("uuid-9", d.RefId);
        Assert.Equal("ai", d.Detail);
    }

    [Fact]
    public void Gifted_subs_by_username_leave_ids_for_the_projector()
    {
        const string data = """{ "gifter_username": "Frank", "gifted_usernames": ["Gina", "Hank"], "gifter_total": 10 }""";
        var events = RealtimeChatProjectionMapper.Map(Row("GiftedSubscriptionsEvent", data), webhookCoversChannel: false).Events;
        Assert.Equal(2, events.Count);
        Assert.All(events, e => { Assert.Equal("", e.UserId); Assert.Equal("Frank", e.Username); Assert.Null(e.CounterpartUserId); });
        Assert.Equal(new[] { "Gina", "Hank" }, events.Select(e => e.CounterpartUsername));
    }

    [Fact]
    public void Followers_updated_counts_only_explicit_follows()
    {
        Assert.Empty(RealtimeChatProjectionMapper.Map(Row("FollowersUpdated", """{ "followersCount": 10 }"""), false).Events);
        var f = Assert.Single(RealtimeChatProjectionMapper.Map(
            Row("FollowersUpdated", """{ "followersCount": 11, "username": "Fan", "followed": true }"""), false).Events);
        Assert.Equal(ChatterEventKind.Follow, f.Kind);
        Assert.Equal("Fan", f.Username);
    }

    [Fact]
    public void Unknown_events_and_bad_json_yield_nothing()
    {
        Assert.Same(ChatProjection.Empty, RealtimeChatProjectionMapper.Map(Row("PollUpdateEvent", """{ "poll": {} }"""), false));
        Assert.Same(ChatProjection.Empty, RealtimeChatProjectionMapper.Map(Row("ChatMessageEvent", "{oops"), false));
    }
}
