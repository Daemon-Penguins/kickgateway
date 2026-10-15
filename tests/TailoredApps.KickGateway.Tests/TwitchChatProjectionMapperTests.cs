using TailoredApps.Integrations.Twitch.EventSub;
using TailoredApps.KickGateway.Api.Analytics;
using TailoredApps.KickGateway.Api.Data;
using Xunit;

namespace TailoredApps.KickGateway.Tests;

public class TwitchChatProjectionMapperTests
{
    private static readonly DateTime Received = new(2026, 10, 15, 12, 0, 5, DateTimeKind.Utc);
    private static readonly DateTime Stamped = new(2026, 10, 15, 12, 0, 4, DateTimeKind.Utc);

    private static ReceivedTwitchEvent Row(string type, string data, string key = "es|m-1") => new()
    {
        DedupeKey = key,
        EventType = type,
        EventVersion = "1",
        BroadcasterUserId = "22484632",
        BroadcasterLogin = "forsen",
        ReceivedAt = Received,
        TwitchTimestamp = Stamped,
        RawData = data,
    };

    private const string Chat = """
        { "broadcaster_user_id": "22484632", "chatter_user_id": "141981764", "chatter_user_login": "twitchdev", "chatter_user_name": "TwitchDev",
          "message_id": "cc106a89-1814-919d-454c-f4f2f970aae7",
          "message": { "text": "Hi @Forsen and @carol_99 Kappa cheer100", "fragments": [
            { "type": "text", "text": "Hi " }, { "type": "mention", "text": "@Forsen", "mention": { "user_id": "22484632", "user_name": "Forsen", "user_login": "forsen" } },
            { "type": "text", "text": " and @carol_99 Kappa " }, { "type": "cheermote", "text": "cheer100", "cheermote": { "prefix": "cheer", "bits": 100, "tier": 100 } } ] },
          "color": "#00FF7F", "badges": [ { "set_id": "partner", "id": "1", "info": "" }, { "set_id": "subscriber", "id": "12", "info": "16" } ],
          "message_type": "text", "cheer": { "bits": 100 },
          "reply": { "parent_message_id": "p-1", "parent_message_body": "hello", "parent_user_id": "555", "parent_user_name": "Alice", "parent_user_login": "alice" } }
        """;

    [Fact]
    public void Chat_message_maps_to_a_twitch_platform_row_with_mentions_and_a_cheer_event()
    {
        var p = TwitchChatProjectionMapper.Map(Row(EventSubTypes.ChatMessage, Chat));

        var m = Assert.Single(p.Messages);
        Assert.Equal("cc106a89-1814-919d-454c-f4f2f970aae7", m.MessageId);
        Assert.Equal(ChatPlatforms.Twitch, m.Platform);
        Assert.Equal(ChatSource.Twitch, m.Source);
        Assert.Equal("es|m-1", m.SourceId);
        Assert.Equal("forsen", m.ChannelSlug);
        Assert.Equal("22484632", m.BroadcasterUserId);
        Assert.Equal("141981764", m.SenderUserId);
        Assert.Equal("twitchdev", m.SenderUsername);
        Assert.True(m.SenderIsVerified); // partner badge
        Assert.Equal("partner:1|subscriber:16", m.SenderBadges);
        Assert.Equal("#00FF7F", m.SenderColor);
        Assert.Equal(Stamped, m.CreatedAt);
        Assert.Equal("p-1", m.ReplyToMessageId);
        Assert.Equal("555", m.ReplyToUserId);
        Assert.Equal("alice", m.ReplyToUsername);
        Assert.Equal(new[] { "forsen", "carol_99" }, m.Mentions.Select(x => x.MentionedUsername).ToArray());

        var e = Assert.Single(p.Events);
        Assert.Equal(ChatterEventKind.KicksGift, e.Kind);
        Assert.Equal(100, e.Amount);
        Assert.Equal(ChatPlatforms.Twitch, e.Platform);
        Assert.Equal("tw:es|m-1:cheer", e.Id);
        Assert.Equal("141981764", e.UserId);
        Assert.StartsWith("bits:", e.Detail);
    }

    [Fact]
    public void Resub_notice_is_a_renewal_plus_the_chat_line()
    {
        const string resub = """
            { "broadcaster_user_id": "22484632", "chatter_user_id": "1", "chatter_user_login": "bob", "chatter_user_name": "Bob", "chatter_is_anonymous": false,
              "message_id": "n-1", "message": { "text": "pog", "fragments": [] }, "badges": [], "system_message": "Bob resubscribed",
              "notice_type": "resub", "resub": { "cumulative_months": 7, "duration_months": 1, "streak_months": 3, "sub_tier": "2000", "is_prime": false, "is_gift": false } }
            """;
        var p = TwitchChatProjectionMapper.Map(Row(EventSubTypes.ChatNotification, resub, "es|n-1"));

        var m = Assert.Single(p.Messages);
        Assert.Equal("n-1", m.MessageId);
        Assert.Equal("pog", m.Content);
        var e = Assert.Single(p.Events);
        Assert.Equal(ChatterEventKind.SubscriptionRenewal, e.Kind);
        Assert.Equal(7, e.Amount);
        Assert.Equal("tier2", e.Detail);
        Assert.Equal("bob", e.Username);
        Assert.Equal("tw:es|n-1", e.Id);
    }

    [Fact]
    public void Sub_gift_names_the_gifter_as_actor_and_the_recipient_as_counterpart()
    {
        const string gift = """
            { "broadcaster_user_id": "22484632", "chatter_user_id": "1", "chatter_user_login": "bob", "chatter_user_name": "Bob", "chatter_is_anonymous": false,
              "message_id": "n-2", "message": { "text": "", "fragments": [] }, "badges": [], "system_message": "Bob gifted a sub to Carol",
              "notice_type": "sub_gift", "sub_gift": { "duration_months": 1, "cumulative_total": 5, "recipient_user_id": "77", "recipient_user_name": "Carol", "recipient_user_login": "carol", "sub_tier": "1000", "community_gift_id": "cg-1" } }
            """;
        var p = TwitchChatProjectionMapper.Map(Row(EventSubTypes.ChatNotification, gift, "es|n-2"));

        Assert.Empty(p.Messages);
        var e = Assert.Single(p.Events);
        Assert.Equal(ChatterEventKind.SubscriptionGift, e.Kind);
        Assert.Equal("1", e.UserId);
        Assert.Equal("77", e.CounterpartUserId);
        Assert.Equal("carol", e.CounterpartUsername);
        Assert.Equal(1, e.Amount);
        Assert.Equal("cg-1", e.RefId);

        const string anonymous = """
            { "broadcaster_user_id": "22484632", "chatter_user_id": "", "chatter_user_login": "", "chatter_user_name": "", "chatter_is_anonymous": true,
              "message_id": "n-3", "message": { "text": "", "fragments": [] }, "badges": [], "system_message": "An anonymous user gifted a sub",
              "notice_type": "sub_gift", "sub_gift": { "duration_months": 1, "recipient_user_id": "78", "recipient_user_name": "Dan", "recipient_user_login": "dan", "sub_tier": "1000" } }
            """;
        var anon = Assert.Single(TwitchChatProjectionMapper.Map(Row(EventSubTypes.ChatNotification, anonymous, "es|n-3")).Events);
        Assert.Equal("", anon.UserId);
        Assert.Equal("dan", anon.CounterpartUsername);
    }

    [Fact]
    public void Deletions_and_user_clears_become_moderation_events()
    {
        var del = Assert.Single(TwitchChatProjectionMapper.Map(Row(EventSubTypes.ChatMessageDelete,
            """{"broadcaster_user_id":"22484632","target_user_id":"9","target_user_login":"spammer","target_user_name":"Spammer","message_id":"d-1"}""", "es|d")).Events);
        Assert.Equal(ChatterEventKind.MessageDeleted, del.Kind);
        Assert.Equal("d-1", del.RefId);
        Assert.Equal("9", del.UserId);
        Assert.Equal("spammer", del.Username);

        var cleared = Assert.Single(TwitchChatProjectionMapper.Map(Row(EventSubTypes.ChatClearUserMessages,
            """{"broadcaster_user_id":"22484632","target_user_id":"9","target_user_login":"spammer","target_user_name":"Spammer"}""", "es|c")).Events);
        Assert.Equal(ChatterEventKind.Timeout, cleared.Kind);
        Assert.Equal("clear_user_messages", cleared.Detail);
    }

    [Fact]
    public void Stream_state_and_unknown_types_project_nothing()
    {
        Assert.Empty(TwitchChatProjectionMapper.Map(Row("helix.stream.online", """{"stream_id":"1"}""")).Messages);
        Assert.Empty(TwitchChatProjectionMapper.Map(Row(EventSubTypes.ChatClear, """{"broadcaster_user_id":"22484632"}""")).Events);
        Assert.Empty(TwitchChatProjectionMapper.Map(Row(EventSubTypes.ChatMessage, "{broken")).Messages);
    }
}
