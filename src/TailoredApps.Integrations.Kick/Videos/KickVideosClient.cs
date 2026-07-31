using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TailoredApps.Integrations.Kick.Internal;
using TailoredApps.Integrations.Kick.Models;
using TailoredApps.Integrations.Kick.Sidecar;

namespace TailoredApps.Integrations.Kick.Videos;

public class KickVideosClient : IKickVideosClient
{
    /// <summary>
    /// Concurrent per-video lookups. Each one is a Cloudflare-bypassing sidecar
    /// fetch, so this trades a burst against ~30 sequential round trips for a
    /// full listing; keep it modest so a listing request can't monopolise the
    /// sidecar.
    /// </summary>
    private const int MaxParallelVodLookups = 4;

    /// <summary>Cache guard — a channel's whole history is ~30 entries, so this is generous.</summary>
    private const int MaxCachedVodIds = 5_000;

    private readonly IKickSidecarFetcher _fetcher;
    private readonly KickGlobalDefaults _defaults;
    private readonly ILogger<KickVideosClient> _log;

    /// <summary>
    /// legacy <c>video.uuid</c> → <c>livestream.vod_id</c>. Kick assigns a
    /// broadcast's vod_id once and never changes it, so this never goes stale;
    /// it exists because the poller re-reads the same listing every 90 s.
    /// Instance-level: the client is registered as a singleton.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _vodIds = new(StringComparer.OrdinalIgnoreCase);

    public KickVideosClient(IKickSidecarFetcher fetcher, IOptions<KickGlobalDefaults> defaults, ILogger<KickVideosClient> log)
    {
        _fetcher = fetcher;
        _defaults = defaults.Value;
        _log = log;
    }

    private string WebApiBase => string.IsNullOrWhiteSpace(_defaults.ClipsWebApiBaseUrl)
        ? "https://kick.com"
        : _defaults.ClipsWebApiBaseUrl.TrimEnd('/');

    public async Task<IReadOnlyList<KickVideoInfo>> GetVideosAsync(string slug, CancellationToken ct = default)
    {
        slug = (slug ?? "").Trim().ToLowerInvariant();
        if (slug.Length == 0) return [];

        var json = await _fetcher.FetchAsync($"{WebApiBase}/api/v2/channels/{Uri.EscapeDataString(slug)}/videos", ct);
        if (json is null) return [];

        List<KickVideoInfo> list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array) return [];

            list = new List<KickVideoInfo>(root.GetArrayLength());
            foreach (var el in root.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.Object)
                    list.Add(Parse(el));
            }
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "Failed to parse videos for {Slug}", slug);
            return [];
        }

        return await ResolveVodIdsAsync(list, ct);
    }

    /// <summary>
    /// Fills in each entry's <see cref="KickVideoInfo.VodId"/> — the id that
    /// actually works in <c>kick.com/{slug}/videos/{id}</c>. The channel listing
    /// only carries the legacy <c>video.uuid</c>, which Kick stopped honouring in
    /// watch URLs, so without this every deep link 404s. Failures leave VodId
    /// null rather than dropping the entry: the rest of the metadata is still useful.
    /// </summary>
    private async Task<IReadOnlyList<KickVideoInfo>> ResolveVodIdsAsync(List<KickVideoInfo> videos, CancellationToken ct)
    {
        var resolved = new KickVideoInfo[videos.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, videos.Count),
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelVodLookups, CancellationToken = ct },
            async (i, token) =>
            {
                var video = videos[i];
                resolved[i] = string.IsNullOrWhiteSpace(video.VideoUuid)
                    ? video
                    : video with { VodId = await ResolveVodIdAsync(video.VideoUuid, token) };
            });

        var missing = resolved.Count(v => v.VodId is null && !string.IsNullOrWhiteSpace(v.VideoUuid));
        if (missing > 0)
            _log.LogWarning("Could not resolve vod_id for {Missing}/{Total} video(s) — their deep links would 404",
                missing, resolved.Length);

        return resolved;
    }

    /// <summary>
    /// Legacy uuid → the watch-URL id, via <c>kick.com/api/v1/video/{uuid}</c>
    /// (<c>livestream.vod_id</c>). Cached: the mapping is immutable.
    /// </summary>
    private async Task<string?> ResolveVodIdAsync(string legacyUuid, CancellationToken ct)
    {
        if (_vodIds.TryGetValue(legacyUuid, out var cached))
            return cached;

        var json = await _fetcher.FetchAsync($"{WebApiBase}/api/v1/video/{Uri.EscapeDataString(legacyUuid)}", ct);
        if (json is null)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("livestream", out var livestream)
                || livestream.ValueKind != JsonValueKind.Object)
                return null;

            var vodId = livestream.ReadAsString("vod_id");
            if (string.IsNullOrWhiteSpace(vodId))
                return null;

            if (_vodIds.Count >= MaxCachedVodIds)
                _vodIds.Clear();
            _vodIds[legacyUuid] = vodId;
            return vodId;
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "Failed to parse video {Uuid} while resolving its vod_id", legacyUuid);
            return null;
        }
    }

    private static KickVideoInfo Parse(JsonElement el)
    {
        // The VOD's legacy identifier lives on a nested `video` object. It is the
        // key for /api/v1/video/{uuid}, but NOT the id used in watch URLs — see
        // ResolveVodIdAsync.
        var uuid = "";
        if (el.TryGetProperty("video", out var v) && v.ValueKind == JsonValueKind.Object)
            uuid = v.ReadAsString("uuid");

        return new KickVideoInfo(
            LivestreamId: el.ReadAsString("id"),
            VideoUuid: uuid,
            Title: NullIfEmpty(el.ReadAsString("session_title")),
            StartTimeUtc: ParseDate(el, "start_time") ?? ParseDate(el, "created_at"),
            DurationMs: el.ReadAsLong("duration"),
            IsLive: el.ReadAsBool("is_live"),
            ViewerCount: el.ReadAsInt("viewer_count"));
    }

    private static DateTime? ParseDate(JsonElement parent, string prop)
    {
        if (!parent.TryGetProperty(prop, out var el) || el.ValueKind != JsonValueKind.String) return null;
        var s = el.GetString();
        if (string.IsNullOrEmpty(s)) return null;
        if (el.TryGetDateTime(out var dt)) return dt.ToUniversalTime();
        return DateTime.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt2)
            ? dt2
            : null;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
