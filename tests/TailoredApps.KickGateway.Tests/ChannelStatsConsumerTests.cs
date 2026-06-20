using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using TailoredApps.Integrations.Kick.Channels;
using TailoredApps.Integrations.Kick.Models;
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
            Slug: "xqc", ChannelId: "668", UserId: "676", Username: "XQc",
            FollowersCount: 1_000_000, Verified: true, IsBanned: false,
            VodEnabled: true, SubscriptionEnabled: true, IsAffiliate: true,
            ProfilePicUrl: "https://img/pfp.webp", BannerImageUrl: "https://img/banner.webp",
            PlaybackUrl: "https://stream/hls.m3u8",
            IsLive: true, ViewerCount: 4242, StreamTitle: "live now", StreamStartedAt: null,
            Language: "English", IsMature: false, ThumbnailUrl: "https://img/thumb.webp",
            Category: new KickChannelCategory("15", "Just Chatting", "just-chatting", 500),
            RawJson: "{}");

        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IKickChannelClient>(new StubChannelClient(info))
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

    private sealed class StubChannelClient(KickChannelInfo? info) : IKickChannelClient
    {
        public Task<KickChannelInfo?> GetChannelAsync(string slug, CancellationToken ct = default) => Task.FromResult(info);
    }
}
