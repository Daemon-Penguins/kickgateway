using System.Text.Json;
using TailoredApps.KickGateway.Realtime.Mapping;
using TailoredApps.KickGateway.Realtime.Pusher;
using Xunit;

namespace TailoredApps.KickGateway.Tests;

public class PusherProtocolTests
{
    [Fact]
    public void Parse_double_decodes_data_string()
    {
        var inner = "{\"id\":\"abc\",\"content\":\"hi\"}";
        var outer = JsonSerializer.Serialize(new
        {
            @event = "App\\Events\\ChatMessageEvent",
            channel = "chatrooms.123.v2",
            data = inner, // Pusher sends `data` as a JSON-encoded string
        });

        var frame = PusherConnection.Parse(outer);

        Assert.NotNull(frame);
        Assert.Equal("App\\Events\\ChatMessageEvent", frame!.Event);
        Assert.Equal("chatrooms.123.v2", frame.Channel);
        Assert.Equal(inner, frame.Data); // decoded once → still the inner JSON text
    }

    [Fact]
    public void Parse_handles_object_data_and_missing_channel()
    {
        var frame = PusherConnection.Parse("{\"event\":\"pusher:pong\",\"data\":{}}");
        Assert.NotNull(frame);
        Assert.Equal("pusher:pong", frame!.Event);
        Assert.Null(frame.Channel);
        Assert.Equal("{}", frame.Data);
    }

    [Fact]
    public void Parse_handles_frame_without_data()
    {
        var frame = PusherConnection.Parse("{\"event\":\"pusher:ping\"}");
        Assert.NotNull(frame);
        Assert.Equal("pusher:ping", frame!.Event);
        Assert.Equal("", frame.Data);
    }

    [Theory]
    [InlineData("App\\Events\\ChatMessageEvent", "ChatMessageEvent")]
    [InlineData("App\\Events\\GiftedSubscriptionsEvent", "GiftedSubscriptionsEvent")]
    [InlineData("Plain", "Plain")]
    [InlineData("", "")]
    public void ShortName_strips_laravel_prefix(string input, string expected)
        => Assert.Equal(expected, RealtimeFrameMapper.ShortName(input));

    [Fact]
    public void DedupeKey_uses_natural_id_and_is_stable()
    {
        var f = new PusherFrame("App\\Events\\ChatMessageEvent", "chatrooms.1.v2", "{\"id\":\"x9\"}");
        using var d = JsonDocument.Parse(f.Data);

        var k1 = RealtimeFrameMapper.DedupeKeyFrom(f, d.RootElement, f.Data);
        using var d2 = JsonDocument.Parse(f.Data);
        var k2 = RealtimeFrameMapper.DedupeKeyFrom(f, d2.RootElement, f.Data);

        Assert.Equal(k1, k2);
        Assert.Contains("x9", k1);
        Assert.Contains("ChatMessageEvent", k1);
    }

    [Fact]
    public void DedupeKey_falls_back_to_stable_hash_without_id()
    {
        var f = new PusherFrame("App\\Events\\FollowersUpdated", "channel.1", "{\"followersCount\":5}");
        using var d = JsonDocument.Parse(f.Data);

        var withRoot = RealtimeFrameMapper.DedupeKeyFrom(f, d.RootElement, f.Data);
        var withDefaultRoot = RealtimeFrameMapper.DedupeKeyFrom(f, default, f.Data);

        Assert.Equal(withRoot, withDefaultRoot); // same content → same hash
        Assert.DoesNotContain("followersCount", withRoot);
    }
}
