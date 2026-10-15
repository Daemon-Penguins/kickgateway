using TailoredApps.Integrations.Twitch.EventSub;
using TailoredApps.Integrations.Twitch.Helix;
using TailoredApps.TwitchGateway.Contracts;
using TailoredApps.TwitchGateway.Contracts.Channel;
using TailoredApps.TwitchGateway.Contracts.Chat;
using TailoredApps.TwitchGateway.Listener.Mapping;
using TailoredApps.TwitchGateway.Listener.Roster;
using Xunit;

namespace TailoredApps.KickGateway.Tests;

public class TwitchEventMapperTests
{
    private static readonly ManagedTwitchChannel Channel = new("forsen", "22484632", "Forsen", CaptureChat: true, CaptureAudio: false);
    private static readonly DateTime Now = new(2026, 10, 15, 12, 0, 5, DateTimeKind.Utc);

    private static EventSubMessage Msg(string type, string eventJson, string id = "msg-1") =>
        new(id, EventSubProtocol.Notification, Now.AddSeconds(-1), type, "1", null, "sub-1", "enabled", "{}", eventJson);

    // Field names per dev.twitch.tv/docs/eventsub/eventsub-reference (channel.chat.message v1).
    private const string ChatMessage = """
        {
          "broadcaster_user_id": "22484632", "broadcaster_user_login": "forsen", "broadcaster_user_name": "Forsen",
          "chatter_user_id": "141981764", "chatter_user_login": "twitchdev", "chatter_user_name": "TwitchDev",
          "message_id": "cc106a89-1814-919d-454c-f4f2f970aae7",
          "message": { "text": "Hi @Forsen Kappa cheer100", "fragments": [
            { "type": "text", "text": "Hi " },
            { "type": "mention", "text": "@Forsen", "mention": { "user_id": "22484632", "user_name": "Forsen", "user_login": "forsen" } },
            { "type": "text", "text": " " },
            { "type": "emote", "text": "Kappa", "emote": { "id": "25", "emote_set_id": "0", "owner_id": "twitch", "format": ["static"] } },
            { "type": "text", "text": " " },
            { "type": "cheermote", "text": "cheer100", "cheermote": { "prefix": "cheer", "bits": 100, "tier": 100 } } ] },
          "color": "#00FF7F",
          "badges": [ { "set_id": "moderator", "id": "1", "info": "" }, { "set_id": "subscriber", "id": "12", "info": "16" } ],
          "message_type": "text",
          "cheer": { "bits": 100 },
          "reply": { "parent_message_id": "p-1", "parent_message_body": "hello", "parent_user_id": "555", "parent_user_name": "Alice", "parent_user_login": "alice",
                     "thread_message_id": "p-1", "thread_user_id": "555", "thread_user_name": "Alice", "thread_user_login": "alice" },
          "channel_points_custom_reward_id": null,
          "source_broadcaster_user_id": null, "source_broadcaster_user_login": null, "source_broadcaster_user_name": null, "source_message_id": null, "source_badges": null
        }
        """;

    [Fact]
    public void Maps_chat_message_with_fragments_badges_reply_and_envelope()
    {
        var evt = TwitchEventMapper.Map(Msg(EventSubTypes.ChatMessage, ChatMessage), Channel, Now);

        var chat = Assert.IsType<TwitchChatMessage>(evt);
        Assert.Equal("cc106a89-1814-919d-454c-f4f2f970aae7", chat.ChatMessageId);
        Assert.Equal("Hi @Forsen Kappa cheer100", chat.Text);
        Assert.Equal("141981764", chat.ChatterUserId);
        Assert.Equal("twitchdev", chat.ChatterLogin);
        Assert.Equal("TwitchDev", chat.ChatterName);
        Assert.Equal("#00FF7F", chat.Color);
        Assert.Equal(100, chat.CheerBits);
        Assert.Equal(2, chat.Badges.Length);
        Assert.Equal("subscriber", chat.Badges[1].SetId);
        Assert.Equal("16", chat.Badges[1].Info);
        Assert.Equal(6, chat.Fragments.Length);
        Assert.Equal("mention", chat.Fragments[1].Type);
        Assert.Equal("forsen", chat.Fragments[1].MentionLogin);
        Assert.Equal("25", chat.Fragments[3].EmoteId);
        Assert.Equal(100, chat.Fragments[5].CheermoteBits);
        Assert.NotNull(chat.Reply);
        Assert.Equal("p-1", chat.Reply!.ParentMessageId);
        Assert.Equal("alice", chat.Reply.ParentLogin);
        Assert.Null(chat.ChannelPointsCustomRewardId);
        Assert.Null(chat.SourceBroadcasterUserId);

        // envelope
        Assert.Equal("forsen", chat.BroadcasterLogin);
        Assert.Equal("22484632", chat.BroadcasterUserId);
        Assert.Equal("Forsen", chat.BroadcasterName);
        Assert.Equal(EventSubTypes.ChatMessage, chat.EventType);
        Assert.Equal("1", chat.EventVersion);
        Assert.Equal("msg-1", chat.MessageId);
        Assert.Equal("es|msg-1", chat.DedupeKey);
        Assert.Equal(Now, chat.ReceivedAt);
        Assert.Equal(Now.AddSeconds(-1), chat.TwitchTimestamp);
        Assert.Contains("cheer100", chat.RawPayload);
    }

    [Fact]
    public void Maps_resub_notification_with_the_typed_notice_and_shared_chat_variant()
    {
        const string resub = """
            { "broadcaster_user_id": "22484632", "broadcaster_user_login": "forsen", "broadcaster_user_name": "Forsen",
              "chatter_user_id": "1", "chatter_user_login": "bob", "chatter_user_name": "Bob", "chatter_is_anonymous": false, "color": "",
              "badges": [], "system_message": "Bob subscribed at Tier 1. They've subscribed for 7 months!", "message_id": "n-1",
              "message": { "text": "pog", "fragments": [ { "type": "text", "text": "pog" } ] },
              "notice_type": "resub", "sub": null,
              "resub": { "cumulative_months": 7, "duration_months": 1, "streak_months": 3, "sub_tier": "1000", "is_prime": false, "is_gift": false, "gifter_is_anonymous": null, "gifter_user_id": null, "gifter_user_name": null, "gifter_user_login": null },
              "sub_gift": null, "community_sub_gift": null, "gift_paid_upgrade": null, "prime_paid_upgrade": null, "pay_it_forward": null, "raid": null, "unraid": null, "announcement": null, "bits_badge_tier": null, "charity_donation": null }
            """;
        var evt = TwitchEventMapper.Map(Msg(EventSubTypes.ChatNotification, resub), Channel, Now);
        var n = Assert.IsType<TwitchChatNotification>(evt);
        Assert.Equal("resub", n.NoticeType);
        Assert.NotNull(n.Resub);
        Assert.Equal(7, n.Resub!.CumulativeMonths);
        Assert.Equal("1000", n.Resub.Tier);
        Assert.Null(n.Sub);
        Assert.Equal("pog", n.Text);
        Assert.Contains("7 months", n.SystemMessage);

        const string sharedRaid = """
            { "broadcaster_user_id": "22484632", "chatter_user_id": "2", "chatter_user_login": "raider", "chatter_user_name": "Raider", "chatter_is_anonymous": false,
              "notice_type": "shared_chat_raid", "message_id": "n-2", "system_message": "", "message": { "text": "", "fragments": [] }, "badges": [],
              "shared_chat_raid": { "user_id": "2", "user_login": "raider", "user_name": "Raider", "viewer_count": 1234, "profile_image_url": "https://x/y.png" },
              "source_broadcaster_user_id": "777", "source_broadcaster_user_login": "other", "source_message_id": "n-2-src", "is_source_only": true }
            """;
        var shared = Assert.IsType<TwitchChatNotification>(TwitchEventMapper.Map(Msg(EventSubTypes.ChatNotification, sharedRaid, "msg-2"), Channel, Now));
        Assert.Equal("shared_chat_raid", shared.NoticeType);
        Assert.NotNull(shared.Raid);
        Assert.Equal(1234, shared.Raid!.ViewerCount);
        Assert.Equal("777", shared.SourceBroadcasterUserId);
        Assert.True(shared.IsSourceOnly);
    }

    [Fact]
    public void Maps_delete_clear_and_settings()
    {
        var del = Assert.IsType<TwitchChatMessageDeleted>(TwitchEventMapper.Map(Msg(EventSubTypes.ChatMessageDelete,
            """{"broadcaster_user_id":"22484632","target_user_id":"9","target_user_login":"spammer","target_user_name":"Spammer","message_id":"d-1"}"""), Channel, Now));
        Assert.Equal("9", del.TargetUserId);
        Assert.Equal("d-1", del.ChatMessageId);

        Assert.IsType<TwitchChatCleared>(TwitchEventMapper.Map(Msg(EventSubTypes.ChatClear, """{"broadcaster_user_id":"22484632"}"""), Channel, Now));

        var settings = Assert.IsType<TwitchChatSettingsUpdated>(TwitchEventMapper.Map(Msg(EventSubTypes.ChatSettingsUpdate,
            """{"broadcaster_user_id":"22484632","emote_mode":true,"follower_mode":true,"follower_mode_duration_minutes":10,"slow_mode":false,"slow_mode_wait_time_seconds":null,"subscriber_mode":false,"unique_chat_mode":false}"""), Channel, Now));
        Assert.True(settings.EmoteMode);
        Assert.Equal(10, settings.FollowerModeDurationMinutes);
        Assert.Null(settings.SlowModeWaitTimeSeconds);
    }

    [Fact]
    public void Unknown_type_and_broken_json_fall_through_to_unknown_with_raw_payload()
    {
        var unknown = TwitchEventMapper.Map(Msg("channel.hype_train.begin", """{"broadcaster_user_id":"22484632","total":1}"""), Channel, Now);
        var u = Assert.IsType<TwitchEventUnknown>(unknown);
        Assert.Equal("channel.hype_train.begin", u.EventType);
        Assert.Contains("\"total\":1", u.RawPayload);

        var broken = TwitchEventMapper.Map(Msg(EventSubTypes.ChatMessage, "{not json"), Channel, Now);
        Assert.IsType<TwitchEventUnknown>(broken);
        Assert.Equal("es|msg-1", broken.DedupeKey);
    }

    [Fact]
    public void Helix_stream_state_maps_to_synthetic_events_with_stable_dedupe_keys()
    {
        var stream = new TwitchStream("41375541868", "22484632", "forsen", "Forsen", "509658", "Just Chatting", "live", "monday", 15000,
            new DateTime(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc), "en", false);

        var online = TwitchEventMapper.StreamOnline(stream, Channel, Now, "{}");
        Assert.Equal("helix.stream.online", online.EventType);
        Assert.Equal("helix|online|41375541868", online.DedupeKey);
        Assert.Equal("Just Chatting", online.CategoryName);
        Assert.Equal(15000, online.ViewerCount);

        var updated = TwitchEventMapper.ChannelUpdated(stream, Channel, Now, "{}");
        Assert.StartsWith("helix|update|41375541868|", updated.DedupeKey);
        Assert.Equal(updated.DedupeKey, TwitchEventMapper.ChannelUpdated(stream, Channel, Now.AddMinutes(1), "{}").DedupeKey); // same title/category -> same key
        Assert.NotEqual(updated.DedupeKey, TwitchEventMapper.ChannelUpdated(stream with { Title = "tuesday" }, Channel, Now, "{}").DedupeKey);

        var offline = TwitchEventMapper.StreamOffline("41375541868", stream.StartedAt, Channel, Now);
        Assert.IsType<TwitchStreamOffline>(offline);
        Assert.Equal("helix|offline|41375541868", offline.DedupeKey);
        Assert.Equal(Now, offline.EndedAt);
    }
}
