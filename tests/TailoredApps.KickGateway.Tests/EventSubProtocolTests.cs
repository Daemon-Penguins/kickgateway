using TailoredApps.Integrations.Twitch.EventSub;
using TailoredApps.TwitchGateway.Listener.EventSub;
using Xunit;

namespace TailoredApps.KickGateway.Tests;

public class EventSubProtocolTests
{
    [Fact]
    public void Parses_welcome_with_session()
    {
        var msg = EventSubProtocol.Parse("""
            {"metadata":{"message_id":"96a3f3b5-5dec-4eed-908e-e11ee657416c","message_type":"session_welcome","message_timestamp":"2026-10-15T12:00:00.123456789Z"},
             "payload":{"session":{"id":"AQoQexAWVYKSTIu4ec_2VAxyuhAB","status":"connected","connected_at":"2026-10-15T12:00:00.1Z","keepalive_timeout_seconds":10,"reconnect_url":null}}}
            """);

        Assert.NotNull(msg);
        Assert.True(msg!.IsWelcome);
        Assert.Equal("AQoQexAWVYKSTIu4ec_2VAxyuhAB", msg.Session!.Id);
        Assert.Equal(10, msg.Session.KeepaliveTimeoutSeconds);
        Assert.Null(msg.Session.ReconnectUrl);
        // Twitch stamps nanoseconds; .NET keeps 100 ns ticks and rounds the rest (…123456789 -> …1234568).
        Assert.Equal(new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc).AddTicks(1234568), msg.Timestamp);
    }

    [Fact]
    public void Parses_notification_with_event_and_subscription()
    {
        var msg = EventSubProtocol.Parse("""
            {"metadata":{"message_id":"befa7b53-d79d-478f-86b9-120f112b044e","message_type":"notification","message_timestamp":"2026-10-15T12:00:05Z","subscription_type":"channel.chat.message","subscription_version":"1"},
             "payload":{"subscription":{"id":"f1c2a387-161a-49f9-a165-0f21d7a4e1c4","status":"enabled","type":"channel.chat.message","version":"1","cost":0,"condition":{"broadcaster_user_id":"1337","user_id":"9001"},"transport":{"method":"websocket","session_id":"AQoQexAWVYKSTIu4ec_2VAxyuhAB"},"created_at":"2026-10-15T12:00:01Z"},
                        "event":{"broadcaster_user_id":"1337","chatter_user_id":"4242","message_id":"m-1","message":{"text":"hi","fragments":[]}}}}
            """);

        Assert.NotNull(msg);
        Assert.True(msg!.IsNotification);
        Assert.Equal("channel.chat.message", msg.SubscriptionType);
        Assert.Equal("1", msg.SubscriptionVersion);
        Assert.Equal("f1c2a387-161a-49f9-a165-0f21d7a4e1c4", msg.SubscriptionId);
        Assert.Equal("enabled", msg.SubscriptionStatus);
        Assert.Contains("\"message_id\":\"m-1\"", msg.EventJson);
        Assert.Contains("\"cost\":0", msg.SubscriptionJson);
    }

    [Fact]
    public void Parses_keepalive_reconnect_and_revocation()
    {
        var keepalive = EventSubProtocol.Parse("""{"metadata":{"message_id":"k","message_type":"session_keepalive","message_timestamp":"2026-10-15T12:00:10Z"},"payload":{}}""");
        Assert.True(keepalive!.IsKeepalive);
        Assert.Null(keepalive.EventJson);

        var reconnect = EventSubProtocol.Parse("""
            {"metadata":{"message_id":"r","message_type":"session_reconnect","message_timestamp":"2026-10-15T12:00:20Z"},
             "payload":{"session":{"id":"AQoQexAWVYKSTIu4ec_2VAxyuhAB","status":"reconnecting","keepalive_timeout_seconds":null,"reconnect_url":"wss://eventsub.wss.twitch.tv/ws?challenge=abc&id=xyz","connected_at":"2026-10-15T12:00:00Z"}}}
            """);
        Assert.True(reconnect!.IsReconnect);
        Assert.Equal("wss://eventsub.wss.twitch.tv/ws?challenge=abc&id=xyz", reconnect.Session!.ReconnectUrl);
        Assert.Null(reconnect.Session.KeepaliveTimeoutSeconds);

        var revocation = EventSubProtocol.Parse("""
            {"metadata":{"message_id":"v","message_type":"revocation","message_timestamp":"2026-10-15T12:00:30Z","subscription_type":"channel.chat.message","subscription_version":"1"},
             "payload":{"subscription":{"id":"sub-1","status":"authorization_revoked","type":"channel.chat.message","version":"1","cost":0,"condition":{},"transport":{"method":"websocket","session_id":"s"},"created_at":"2026-10-15T12:00:01Z"}}}
            """);
        Assert.True(revocation!.IsRevocation);
        Assert.Equal("sub-1", revocation.SubscriptionId);
        Assert.Equal("authorization_revoked", revocation.SubscriptionStatus);
    }

    [Fact]
    public void Non_eventsub_json_is_null()
    {
        Assert.Null(EventSubProtocol.Parse("{\"event\":\"pusher:ping\"}"));
        Assert.Null(EventSubProtocol.Parse("[]"));
    }

    [Theory]
    [InlineData("wss://eventsub.wss.twitch.tv/ws", 30, "wss://eventsub.wss.twitch.tv/ws?keepalive_timeout_seconds=30")]
    [InlineData("wss://eventsub.wss.twitch.tv/ws?x=1", 5, "wss://eventsub.wss.twitch.tv/ws?x=1&keepalive_timeout_seconds=10")]
    [InlineData("wss://eventsub.wss.twitch.tv/ws", 9999, "wss://eventsub.wss.twitch.tv/ws?keepalive_timeout_seconds=600")]
    public void WithKeepalive_appends_and_clamps(string url, int seconds, string expected)
        => Assert.Equal(expected, EventSubProtocol.WithKeepalive(url, seconds));

    [Fact]
    public void Chat_condition_names_broadcaster_and_bot()
    {
        var c = EventSubTypes.ChatCondition("1337", "9001");
        Assert.Equal("1337", c["broadcaster_user_id"]);
        Assert.Equal("9001", c["user_id"]);
    }

    [Fact]
    public void Backoff_waits_the_maximum_after_a_refusal_and_resets_after_an_established_session()
    {
        Assert.Equal(60, EventSubConnection.NextBackoff(1, established: false, refused: true, min: 1, max: 60));
        Assert.Equal(1, EventSubConnection.NextBackoff(32, established: true, refused: false, min: 1, max: 60));
        Assert.Equal(2, EventSubConnection.NextBackoff(1, established: false, refused: false, min: 1, max: 60));
        Assert.Equal(60, EventSubConnection.NextBackoff(40, established: false, refused: false, min: 1, max: 60));
    }
}
