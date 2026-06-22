using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TailoredApps.Integrations.Kick;
using TailoredApps.Integrations.Kick.Sidecar;
using TailoredApps.Integrations.Kick.Videos;
using Xunit;

namespace TailoredApps.KickGateway.Tests;

public class KickVideosClientTests
{
    [Fact]
    public async Task Parses_video_list()
    {
        var client = new KickVideosClient(new StubFetcher(VideosJson()), Opts(), NullLogger<KickVideosClient>.Instance);

        var videos = await client.GetVideosAsync("xqc");

        Assert.Equal(2, videos.Count);

        var first = videos[0];
        Assert.Equal("999", first.LivestreamId);
        Assert.Equal("uuid-aaa", first.VideoUuid);
        Assert.Equal("First stream", first.Title);
        Assert.Equal(7_200_000, first.DurationMs);
        Assert.False(first.IsLive);
        Assert.Equal(1234, first.ViewerCount);
        Assert.NotNull(first.StartTimeUtc);
        Assert.Equal(DateTimeKind.Utc, first.StartTimeUtc!.Value.Kind);

        // Live entry with a missing start_time falls back to created_at; null title stays null.
        var second = videos[1];
        Assert.Equal("1000", second.LivestreamId);
        Assert.Equal("uuid-bbb", second.VideoUuid);
        Assert.True(second.IsLive);
        Assert.Null(second.Title);
        Assert.NotNull(second.StartTimeUtc);
    }

    [Fact]
    public async Task Returns_empty_when_fetch_fails()
    {
        var client = new KickVideosClient(new StubFetcher(null), Opts(), NullLogger<KickVideosClient>.Instance);
        Assert.Empty(await client.GetVideosAsync("xqc"));
    }

    [Fact]
    public async Task Returns_empty_for_non_array_payload()
    {
        var client = new KickVideosClient(new StubFetcher("{\"message\":\"not found\"}"), Opts(), NullLogger<KickVideosClient>.Instance);
        Assert.Empty(await client.GetVideosAsync("xqc"));
    }

    private static IOptions<KickGlobalDefaults> Opts() => Options.Create(new KickGlobalDefaults());

    private static string VideosJson() => JsonSerializer.Serialize(new object[]
    {
        new
        {
            id = 999,
            session_title = "First stream",
            start_time = "2026-06-21T10:00:00Z",
            duration = 7_200_000,
            is_live = false,
            viewer_count = 1234,
            video = new { uuid = "uuid-aaa" },
        },
        new
        {
            id = 1000,
            session_title = (string?)null,
            created_at = "2026-06-22T08:30:00Z",
            duration = 0,
            is_live = true,
            viewer_count = 42,
            video = new { uuid = "uuid-bbb" },
        },
    });

    private sealed class StubFetcher(string? body) : IKickSidecarFetcher
    {
        public Task<string?> FetchAsync(string kickUrl, CancellationToken ct = default) => Task.FromResult(body);
    }
}
