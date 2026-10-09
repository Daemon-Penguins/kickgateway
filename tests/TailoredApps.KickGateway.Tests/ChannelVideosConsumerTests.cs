using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using TailoredApps.Integrations.Kick.Models;
using TailoredApps.Integrations.Kick.Videos;
using TailoredApps.KickGateway.Api.Channels;
using TailoredApps.KickGateway.Contracts.Channels;
using Xunit;

namespace TailoredApps.KickGateway.Tests;

public class ChannelVideosConsumerTests
{
    [Fact]
    public async Task Publishes_ChannelVideos_on_request()
    {
        var videos = new List<KickVideoInfo>
        {
            new("999", "uuid-aaa", "First stream", new DateTime(2026, 6, 21, 10, 0, 0, DateTimeKind.Utc), 7_200_000, false, 1234,
                VodId: "019fb1dc-07d8-77fd-94c9-00c504d72bbc"),
            new("1000", "uuid-bbb", null, new DateTime(2026, 6, 22, 8, 30, 0, DateTimeKind.Utc), 0, true, 42),
        };

        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IKickVideosClient>(new StubVideosClient(videos))
            .AddMassTransitTestHarness(x => x.AddConsumer<ChannelVideosConsumer>())
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(new ChannelVideosRequested { BroadcasterSlug = "xqc" });

            Assert.True(await harness.Consumed.Any<ChannelVideosRequested>());
            Assert.True(await harness.Published.Any<ChannelVideos>());

            var msg = harness.Published.Select<ChannelVideos>().First().Context.Message;
            Assert.True(msg.Success);
            Assert.Equal("xqc", msg.BroadcasterSlug);
            Assert.Equal(2, msg.Videos.Count);
            Assert.Equal("uuid-aaa", msg.Videos[0].VideoUuid);
            Assert.Equal(7_200_000, msg.Videos[0].DurationMs);
            Assert.True(msg.Videos[1].IsLive);
            // The watch URL is precomputed from VodId - consumers must never build one from VideoUuid (404).
            Assert.Equal("https://kick.com/xqc/videos/019fb1dc-07d8-77fd-94c9-00c504d72bbc", msg.Videos[0].WatchUrl);
            Assert.Null(msg.Videos[1].WatchUrl);
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task Publishes_success_with_empty_list_when_channel_has_no_videos()
    {
        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IKickVideosClient>(new StubVideosClient([]))
            .AddMassTransitTestHarness(x => x.AddConsumer<ChannelVideosConsumer>())
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(new ChannelVideosRequested { BroadcasterSlug = "ghost" });

            Assert.True(await harness.Published.Any<ChannelVideos>());
            var msg = harness.Published.Select<ChannelVideos>().First().Context.Message;
            Assert.True(msg.Success);
            Assert.Equal("ghost", msg.BroadcasterSlug);
            Assert.Empty(msg.Videos);
        }
        finally
        {
            await harness.Stop();
        }
    }

    private sealed class StubVideosClient(IReadOnlyList<KickVideoInfo> videos) : IKickVideosClient
    {
        public Task<IReadOnlyList<KickVideoInfo>> GetVideosAsync(string slug, CancellationToken ct = default) => Task.FromResult(videos);
        public Task<string?> GetLiveVodIdAsync(string slug, string livestreamId, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }
}
