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

    /// <summary>
    /// The whole point of the per-video lookup: the listing's <c>video.uuid</c>
    /// is NOT what kick.com puts in a watch URL. Without VodId every deep link
    /// we hand out 404s.
    /// </summary>
    [Fact]
    public async Task Resolves_the_watch_url_id_from_the_per_video_endpoint()
    {
        var client = new KickVideosClient(new StubFetcher(VideosJson()), Opts(), NullLogger<KickVideosClient>.Instance);

        var videos = await client.GetVideosAsync("xqc");

        Assert.Equal("019fb1dc-07d8-77fd-94c9-00c504d72bbc", videos[0].VodId);
        Assert.Equal("019fb715-4f98-75ff-acf4-32d102f77b8c", videos[1].VodId);
    }

    [Fact]
    public async Task Repeated_calls_resolve_each_video_only_once()
    {
        var fetcher = new StubFetcher(VideosJson());
        var client = new KickVideosClient(fetcher, Opts(), NullLogger<KickVideosClient>.Instance);

        await client.GetVideosAsync("xqc");
        var afterFirst = fetcher.VideoLookups;
        await client.GetVideosAsync("xqc");

        // A poll tick every 90s must not re-resolve ids that can never change.
        Assert.Equal(2, afterFirst);
        Assert.Equal(2, fetcher.VideoLookups);
    }

    [Fact]
    public async Task A_failed_lookup_leaves_VodId_null_without_losing_the_entry()
    {
        var fetcher = new StubFetcher(VideosJson(), resolveVideos: false);
        var client = new KickVideosClient(fetcher, Opts(), NullLogger<KickVideosClient>.Instance);

        var videos = await client.GetVideosAsync("xqc");

        Assert.Equal(2, videos.Count);
        Assert.All(videos, v => Assert.Null(v.VodId));
        Assert.Equal("uuid-aaa", videos[0].VideoUuid); // rest of the metadata survives
    }

    /// <summary>
    /// What a stats consumer needs when a channel goes live: the watch-URL id of the broadcast in
    /// progress, found through the listing by livestream id and resolved like any other entry.
    /// </summary>
    [Fact]
    public async Task Resolves_the_live_broadcast_vod_id_by_livestream_id_and_caches_it()
    {
        var fetcher = new StubFetcher(VideosJson());
        var client = new KickVideosClient(fetcher, Opts(), NullLogger<KickVideosClient>.Instance);

        Assert.Equal("019fb715-4f98-75ff-acf4-32d102f77b8c", await client.GetLiveVodIdAsync("xqc", "1000"));
        Assert.Equal(1, fetcher.VideoLookups);

        // Second call for the same broadcast: no listing, no lookup.
        Assert.Equal("019fb715-4f98-75ff-acf4-32d102f77b8c", await client.GetLiveVodIdAsync("xqc", "1000"));
        Assert.Equal(1, fetcher.VideoLookups);

        // Unknown broadcast (e.g. Kick has not created the video entry yet) -> null, nothing resolved.
        Assert.Null(await client.GetLiveVodIdAsync("xqc", "424242"));
        Assert.Equal(1, fetcher.VideoLookups);
    }

    [Fact]
    public async Task A_full_listing_warms_the_live_vod_cache()
    {
        var fetcher = new StubFetcher(VideosJson());
        var client = new KickVideosClient(fetcher, Opts(), NullLogger<KickVideosClient>.Instance);

        await client.GetVideosAsync("xqc");
        var lookupsAfterListing = fetcher.VideoLookups;

        Assert.Equal("019fb1dc-07d8-77fd-94c9-00c504d72bbc", await client.GetLiveVodIdAsync("xqc", "999"));
        Assert.Equal(lookupsAfterListing, fetcher.VideoLookups);
    }

    [Fact]
    public async Task Live_vod_id_is_null_when_the_per_video_lookup_fails()
    {
        var client = new KickVideosClient(new StubFetcher(VideosJson(), resolveVideos: false), Opts(), NullLogger<KickVideosClient>.Instance);
        Assert.Null(await client.GetLiveVodIdAsync("xqc", "1000"));
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

    private static IOptions<KickGlobalDefaults> Opts() =>
        Options.Create(new KickGlobalDefaults { ClipsFetcherUrl = "http://sidecar" });

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

    /// <summary>
    /// Answers both calls the client makes: the channel listing, and the
    /// per-video lookup that carries <c>livestream.vod_id</c> (the shape kick.com
    /// actually returns — the vod id hangs off the nested livestream, not the video).
    /// </summary>
    private sealed class StubFetcher(string? listing, bool resolveVideos = true) : IKickSidecarFetcher
    {
        private static readonly Dictionary<string, string> VodIds = new()
        {
            ["uuid-aaa"] = "019fb1dc-07d8-77fd-94c9-00c504d72bbc",
            ["uuid-bbb"] = "019fb715-4f98-75ff-acf4-32d102f77b8c",
        };

        private int _videoLookups;

        public int VideoLookups => Volatile.Read(ref _videoLookups);

        public Task<string?> FetchAsync(string kickUrl, CancellationToken ct = default)
        {
            const string marker = "/api/v1/video/";
            var at = kickUrl.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0)
                return Task.FromResult(listing);

            Interlocked.Increment(ref _videoLookups);
            if (!resolveVideos)
                return Task.FromResult<string?>(null);

            var uuid = kickUrl[(at + marker.Length)..];
            var body = JsonSerializer.Serialize(new
            {
                uuid,
                livestream = new { id = 1, vod_id = VodIds.GetValueOrDefault(uuid) },
            });
            return Task.FromResult<string?>(body);
        }
    }
}
