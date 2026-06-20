using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TailoredApps.Integrations.Kick;
using TailoredApps.Integrations.Kick.Clips;
using Xunit;

namespace TailoredApps.KickGateway.Tests;

public class KickClipsClientTests
{
    [Fact]
    public async Task GetChannelClips_paginates_filters_mature_and_preserves_order()
    {
        // Page 1 (no cursor): two real clips + one mature (must be dropped), nextCursor set.
        // Page 2 (cursor=CUR1): one more clip, empty cursor → stop.
        var handler = new StubHandler(req =>
        {
            var kickUrl = QueryParam(req.RequestUri!, "url");
            return kickUrl.Contains("cursor=CUR1")
                ? (HttpStatusCode.OK, Page("", ("clip_C", false)))
                : (HttpStatusCode.OK, Page("CUR1", ("clip_A", false), ("clip_B", false), ("clip_M", true)));
        });

        var client = new KickClipsClient(new StubFactory(handler), Opts(), NullLogger<KickClipsClient>.Instance);

        var clips = await client.GetChannelClipsAsync("XQC", maxPages: 3);

        // Mature filtered; rest in newest-first (API) order across both pages.
        Assert.Equal(new[] { "clip_A", "clip_B", "clip_C" }, clips.Select(c => c.Id).ToArray());
        Assert.All(clips, c => Assert.False(c.IsMature));

        // Exactly two upstream fetches; the second carried the cursor and the slug was lowercased.
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("channels/xqc/clips", DecodedKickUrl(handler.Requests[0]));
        Assert.Contains("cursor=CUR1", DecodedKickUrl(handler.Requests[1]));

        // The shared secret header is forwarded to the sidecar.
        Assert.Contains("sekret", handler.Secrets);

        // Fields parsed correctly.
        var a = clips[0];
        Assert.Equal("https://clips.kick.com/clips/71/clip_A/playlist.m3u8", a.VideoUrl);
        Assert.Equal(30, a.DurationSeconds);
        Assert.Equal("xqc", a.ChannelSlug);
        Assert.Equal("xQc", a.CreatorUsername);
    }

    [Fact]
    public async Task GetChannelClips_returns_empty_when_fetcher_unconfigured()
    {
        var handler = new StubHandler(_ => (HttpStatusCode.OK, Page("", ("clip_A", false))));
        var opts = Options.Create(new KickGlobalDefaults { ClipsFetcherUrl = "" }); // not configured
        var client = new KickClipsClient(new StubFactory(handler), opts, NullLogger<KickClipsClient>.Instance);

        var clips = await client.GetChannelClipsAsync("xqc", 3);

        Assert.Empty(clips);
        Assert.Empty(handler.Requests); // never calls out when there's no sidecar
    }

    [Fact]
    public async Task GetChannelClips_passes_sort_and_time_to_kick()
    {
        var handler = new StubHandler(_ => (HttpStatusCode.OK, Page("", ("clip_A", false))));
        var client = new KickClipsClient(new StubFactory(handler), Opts(), NullLogger<KickClipsClient>.Instance);

        await client.GetChannelClipsAsync("xqc", maxPages: 1, sort: "view", time: "month");

        var url = DecodedKickUrl(handler.Requests[0]);
        Assert.Contains("sort=view", url);
        Assert.Contains("time=month", url);
    }

    // --- helpers ---

    private static IOptions<KickGlobalDefaults> Opts() => Options.Create(new KickGlobalDefaults
    {
        ClipsWebApiBaseUrl = "https://kick.com",
        ClipsFetcherUrl = "http://sidecar:8080",
        ClipsFetcherSecret = "sekret",
        ClipsExcludeMature = true,
        ClipsMaxPages = 3,
    });

    private static string DecodedKickUrl(string fetcherRequestUri) =>
        QueryParam(new Uri(fetcherRequestUri), "url");

    /// <summary>Reads a single query-string value without System.Web (the inner kick URL is fully escaped, so '&amp;' splitting is safe).</summary>
    private static string QueryParam(Uri uri, string key)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = pair.IndexOf('=');
            if (i > 0 && pair[..i] == key)
                return Uri.UnescapeDataString(pair[(i + 1)..]);
        }
        return "";
    }

    private static string Page(string nextCursor, params (string id, bool mature)[] clips) =>
        JsonSerializer.Serialize(new
        {
            clips = clips.Select(c => new
            {
                id = c.id,
                title = $"title {c.id}",
                video_url = $"https://clips.kick.com/clips/71/{c.id}/playlist.m3u8",
                thumbnail_url = $"https://clips.kick.com/{c.id}/thumb.webp",
                duration = 30,
                view_count = 5,
                created_at = "2026-06-20T10:00:00Z",
                is_mature = c.mature,
                privacy = "public",
                channel = new { slug = "xqc" },
                creator = new { username = "xQc" },
                category = new { name = "Just Chatting" },
            }).ToArray(),
            nextCursor,
        });

    private sealed class StubHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> responder) : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();
        public List<string> Secrets { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.ToString());
            if (request.Headers.TryGetValues("X-Fetch-Secret", out var v))
                Secrets.Add(string.Join("", v));
            var (code, body) = responder(request);
            return Task.FromResult(new HttpResponseMessage(code)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
