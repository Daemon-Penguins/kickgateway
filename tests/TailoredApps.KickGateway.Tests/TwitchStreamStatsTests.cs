using TailoredApps.Integrations.Twitch.Helix;
using TailoredApps.KickGateway.Api.Channels;
using TailoredApps.TwitchGateway.Contracts.Channel;
using Xunit;

namespace TailoredApps.KickGateway.Tests;

public class TwitchStreamStatsTests
{
    private static readonly DateTime Now = new(2026, 10, 15, 20, 0, 0, DateTimeKind.Utc);
    private static readonly TwitchUser User = new("30454970", "buraczeknadaje", "BuraczekNadaje", "https://x/pic.png", "affiliate");

    private static TwitchStream Live(string id = "318038862051") =>
        new(id, "30454970", "buraczeknadaje", "BuraczekNadaje", "509658", "Just Chatting", "live", "majsterkowanie",
            98, new DateTime(2026, 10, 15, 16, 8, 55, DateTimeKind.Utc), "pl", false);

    private static TwitchVideo Vod(string id, string streamId, DateTime createdAt) =>
        new(id, streamId, "30454970", "buraczeknadaje", "majsterkowanie", createdAt, createdAt, $"https://www.twitch.tv/videos/{id}", "archive", "3h51m");

    [Fact]
    public void Live_stream_maps_viewers_category_and_the_vod_matched_on_stream_id()
    {
        var vodStart = new DateTime(2026, 10, 15, 16, 9, 2, DateTimeKind.Utc);
        var videos = new[]
        {
            Vod("2311", "318038862051", vodStart),           // the running broadcast
            Vod("2290", "318000000000", vodStart.AddDays(-1)), // yesterday's
        };

        var s = TwitchStreamStatsConsumer.Build("BuraczekNadaje", User, Live(), videos, Now);

        Assert.True(s.Success);
        Assert.True(s.IsLive);
        Assert.Equal("buraczeknadaje", s.BroadcasterLogin);
        Assert.Equal("30454970", s.BroadcasterUserId);
        Assert.Equal("BuraczekNadaje", s.DisplayName);
        Assert.Equal("affiliate", s.BroadcasterType);
        Assert.Equal("318038862051", s.StreamId);
        Assert.Equal(98, s.ViewerCount);
        Assert.Equal("Just Chatting", s.CategoryName);
        Assert.Equal("509658", s.CategoryId);
        Assert.Equal("pl", s.Language);
        Assert.Equal(new DateTime(2026, 10, 15, 16, 8, 55, DateTimeKind.Utc), s.StartedAt);
        Assert.Equal("2311", s.LiveVodId);
        Assert.Equal("https://www.twitch.tv/videos/2311", s.LiveVodUrl);
        Assert.Equal(vodStart, s.LiveVodStartedAt);
        Assert.Equal(Now, s.FetchedAt);
    }

    [Fact]
    public void Live_without_a_matching_archive_has_no_vod_yet()
    {
        var s = TwitchStreamStatsConsumer.Build("buraczeknadaje", User, Live(), [Vod("2290", "318000000000", Now.AddDays(-1))], Now);

        Assert.True(s.IsLive);
        Assert.Null(s.LiveVodId);
        Assert.Null(s.LiveVodUrl);
        Assert.Null(s.LiveVodStartedAt);
    }

    [Fact]
    public void Offline_channel_is_a_successful_snapshot_with_no_stream_fields()
    {
        var s = TwitchStreamStatsConsumer.Build("buraczeknadaje", User, null, [Vod("2290", "318000000000", Now.AddDays(-1))], Now);

        Assert.True(s.Success);
        Assert.False(s.IsLive);
        Assert.Null(s.StreamId);
        Assert.Equal(0, s.ViewerCount);
        Assert.Null(s.Title);
        Assert.Null(s.CategoryName);
        Assert.Null(s.StartedAt);
        Assert.Null(s.LiveVodUrl); // an old archive never becomes "the live VOD"
        Assert.Equal("30454970", s.BroadcasterUserId);
    }

    [Fact]
    public void Watch_urls_and_seek_parameter_follow_twitch_syntax()
    {
        Assert.Equal("https://www.twitch.tv/videos/2311", TwitchWatchUrls.Vod(" 2311 "));
        Assert.Null(TwitchWatchUrls.Vod(""));
        Assert.Null(TwitchWatchUrls.Vod(null));
        Assert.Equal("https://www.twitch.tv/buraczeknadaje", TwitchWatchUrls.Channel("BuraczekNadaje"));

        Assert.Equal("1h2m3s", TwitchWatchUrls.SeekParameter(3723));
        Assert.Equal("0m45s", TwitchWatchUrls.SeekParameter(45));
        Assert.Equal("0m0s", TwitchWatchUrls.SeekParameter(-5));
        Assert.Equal("2h0m0s", TwitchWatchUrls.SeekParameter(7200));
    }
}
