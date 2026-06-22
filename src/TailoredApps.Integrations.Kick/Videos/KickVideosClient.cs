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
    private readonly IKickSidecarFetcher _fetcher;
    private readonly KickGlobalDefaults _defaults;
    private readonly ILogger<KickVideosClient> _log;

    public KickVideosClient(IKickSidecarFetcher fetcher, IOptions<KickGlobalDefaults> defaults, ILogger<KickVideosClient> log)
    {
        _fetcher = fetcher;
        _defaults = defaults.Value;
        _log = log;
    }

    public async Task<IReadOnlyList<KickVideoInfo>> GetVideosAsync(string slug, CancellationToken ct = default)
    {
        slug = (slug ?? "").Trim().ToLowerInvariant();
        if (slug.Length == 0) return [];

        var baseUrl = string.IsNullOrWhiteSpace(_defaults.ClipsWebApiBaseUrl)
            ? "https://kick.com"
            : _defaults.ClipsWebApiBaseUrl.TrimEnd('/');

        var json = await _fetcher.FetchAsync($"{baseUrl}/api/v2/channels/{Uri.EscapeDataString(slug)}/videos", ct);
        if (json is null) return [];

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array) return [];

            var list = new List<KickVideoInfo>(root.GetArrayLength());
            foreach (var el in root.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.Object)
                    list.Add(Parse(el));
            }
            return list;
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "Failed to parse videos for {Slug}", slug);
            return [];
        }
    }

    private static KickVideoInfo Parse(JsonElement el)
    {
        // The VOD's playable identifier lives on a nested `video` object.
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
