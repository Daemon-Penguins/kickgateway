using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TailoredApps.Integrations.Kick;
using TailoredApps.Integrations.Kick.Channels;
using TailoredApps.Integrations.Kick.Sidecar;
using Xunit;

namespace TailoredApps.KickGateway.Tests;

public class KickChannelClientTests
{
    [Fact]
    public async Task Parses_live_channel()
    {
        var client = new KickChannelClient(new StubFetcher(ChannelJson(live: true)), Opts(), NullLogger<KickChannelClient>.Instance);

        var info = await client.GetChannelAsync("xqc");

        Assert.NotNull(info);
        Assert.True(info!.IsLive);
        Assert.Equal(1234, info.ViewerCount);
        Assert.Equal(1_000_000, info.FollowersCount);
        Assert.Equal("XQc", info.Username);
        Assert.True(info.Verified);          // verified is an object upstream
        Assert.False(info.IsBanned);         // is_banned null upstream
        Assert.Equal("hello world", info.StreamTitle);
        Assert.Equal("English", info.Language);
        Assert.NotNull(info.ProfilePicUrl);
        Assert.NotNull(info.BannerImageUrl);
        Assert.NotNull(info.ThumbnailUrl);
        Assert.NotNull(info.StreamStartedAt);
        Assert.Equal("Just Chatting", info.Category?.Name);
        Assert.Equal(500, info.Category?.Viewers);
        Assert.Contains("\"slug\":\"xqc\"", info.RawJson);  // full payload preserved
    }

    [Fact]
    public async Task Parses_offline_channel_and_falls_back_to_recent_category()
    {
        var client = new KickChannelClient(new StubFetcher(ChannelJson(live: false)), Opts(), NullLogger<KickChannelClient>.Instance);

        var info = await client.GetChannelAsync("xqc");

        Assert.NotNull(info);
        Assert.False(info!.IsLive);
        Assert.Equal(0, info.ViewerCount);
        Assert.Null(info.StreamTitle);
        Assert.Equal("Slots", info.Category?.Name);   // from recent_categories[0]
    }

    [Fact]
    public async Task Returns_null_when_fetch_fails()
    {
        var client = new KickChannelClient(new StubFetcher(null), Opts(), NullLogger<KickChannelClient>.Instance);
        Assert.Null(await client.GetChannelAsync("xqc"));
    }

    private static IOptions<KickGlobalDefaults> Opts() => Options.Create(new KickGlobalDefaults());

    private static string ChannelJson(bool live) => JsonSerializer.Serialize(new
    {
        id = 668,
        user_id = 676,
        slug = "xqc",
        is_banned = (object?)null,
        playback_url = "https://stream.example/hls.m3u8",
        vod_enabled = true,
        subscription_enabled = true,
        is_affiliate = true,
        followers_count = 1_000_000,
        banner_image = new { url = "https://img.example/banner.webp" },
        verified = new { id = 1, channel_id = 668 },
        user = new { id = 676, username = "XQc", profile_pic = "https://img.example/pfp.webp" },
        recent_categories = new[] { new { id = 7, name = "Slots", slug = "slots", viewers = 10 } },
        livestream = live
            ? (object)new
            {
                id = 999,
                is_live = true,
                viewer_count = 1234,
                session_title = "hello world",
                start_time = "2026-06-21T10:00:00Z",
                language = "English",
                is_mature = false,
                thumbnail = new { url = "https://img.example/thumb.webp" },
                categories = new[] { new { id = 15, name = "Just Chatting", slug = "just-chatting", viewers = 500 } },
            }
            : null,
    });

    private sealed class StubFetcher(string? body) : IKickSidecarFetcher
    {
        public Task<string?> FetchAsync(string kickUrl, CancellationToken ct = default) => Task.FromResult(body);
    }
}
