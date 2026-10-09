using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using TailoredApps.Integrations.Kick.Channels;
using TailoredApps.Integrations.Kick.Models;
using TailoredApps.Integrations.Kick.Videos;
using TailoredApps.KickGateway.Api.Channels;
using TailoredApps.KickGateway.Contracts.Channels;
using Xunit;

namespace TailoredApps.KickGateway.Tests;

public class ChannelStatsConsumerTests
{
    [Fact]
    public async Task Publishes_ChannelStats_with_viewers_on_request()
    {
        var info = new KickChannelInfo(
            Slug: "xqc", ChannelId: "668", ChatroomId: "12345", UserId: "676", Username: "XQc",
            FollowersCount: 1_000_000, Verified: true, IsBanned: false,
            VodEnabled: true, SubscriptionEnabled: true, IsAffiliate: true,
            ProfilePicUrl: "https://img/pfp.webp", BannerImageUrl: "https://img/banner.webp",
            PlaybackUrl: "https://stream/hls.m3u8",
            IsLive: true, ViewerCount: 4242, StreamTitle: "live now", StreamStartedAt: null,
            Language: "English", IsMature: false, ThumbnailUrl: "https://img/thumb.webp",
            Category: new KickChannelCategory("15", "Just Chatting", "just-chatting", 500),
            RawJson: "{}",
            LivestreamId: "131398385");

        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IKickChannelClient>(new StubChannelClient(info))
            .AddSingleton<IKickVideosClient>(new StubLiveVodClient("131398385", "01a120fc-0248-775f-9585-276216bddf02"))
            .AddMassTransitTestHarness(x => x.AddConsumer<ChannelStatsConsumer>())
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(new ChannelStatsRequested { BroadcasterSlug = "xqc" });

            Assert.True(await harness.Consumed.Any<ChannelStatsRequested>());
            Assert.True(await harness.Published.Any<ChannelStats>());

            var stats = harness.Published.Select<ChannelStats>().First().Context.Message;
            Assert.True(stats.Success);
            Assert.Equal("xqc", stats.BroadcasterSlug);
            Assert.True(stats.IsLive);
            Assert.Equal(4242, stats.ViewerCount);
            Assert.Equal(1_000_000, stats.FollowersCount);
            Assert.Equal("Just Chatting", stats.Category?.Name);
            // The VOD of the stream in progress rides along with the live state.
            Assert.Equal("131398385", stats.LivestreamId);
            Assert.Equal("01a120fc-0248-775f-9585-276216bddf02", stats.LiveVodId);
            Assert.Equal("https://kick.com/xqc/videos/01a120fc-0248-775f-9585-276216bddf02", stats.LiveVodUrl);
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task Publishes_failure_when_channel_not_found()
    {
        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IKickChannelClient>(new StubChannelClient(null))
            .AddSingleton<IKickVideosClient>(new StubLiveVodClient("-", null))
            .AddMassTransitTestHarness(x => x.AddConsumer<ChannelStatsConsumer>())
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(new ChannelStatsRequested { BroadcasterSlug = "ghost" });

            Assert.True(await harness.Published.Any<ChannelStats>());
            var stats = harness.Published.Select<ChannelStats>().First().Context.Message;
            Assert.False(stats.Success);
            Assert.Equal("ghost", stats.BroadcasterSlug);
            Assert.Equal(0, stats.ViewerCount);
        }
        finally
        {
            await harness.Stop();
        }
    }

    private sealed class StubLiveVodClient(string livestreamId, string? vodId) : IKickVideosClient
    {
        public Task<IReadOnlyList<KickVideoInfo>> GetVideosAsync(string slug, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<KickVideoInfo>>([]);

        public Task<string?> GetLiveVodIdAsync(string slug, string id, CancellationToken ct = default) =>
            Task.FromResult(id == livestreamId ? vodId : null);
    }

    private sealed class StubChannelClient(KickChannelInfo? info) : IKickChannelClient
    {
        public Task<KickChannelInfo?> GetChannelAsync(string slug, CancellationToken ct = default) => Task.FromResult(info);
    }
}
