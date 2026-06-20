using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TailoredApps.Integrations.Kick.Internal;
using TailoredApps.Integrations.Kick.Models;
using TailoredApps.Integrations.Kick.Sidecar;

namespace TailoredApps.Integrations.Kick.Channels;

public class KickChannelClient : IKickChannelClient
{
    private readonly IKickSidecarFetcher _fetcher;
    private readonly KickGlobalDefaults _defaults;
    private readonly ILogger<KickChannelClient> _log;

    public KickChannelClient(IKickSidecarFetcher fetcher, IOptions<KickGlobalDefaults> defaults, ILogger<KickChannelClient> log)
    {
        _fetcher = fetcher;
        _defaults = defaults.Value;
        _log = log;
    }

    public async Task<KickChannelInfo?> GetChannelAsync(string slug, CancellationToken ct = default)
    {
        slug = (slug ?? "").Trim().ToLowerInvariant();
        if (slug.Length == 0) return null;

        var baseUrl = string.IsNullOrWhiteSpace(_defaults.ClipsWebApiBaseUrl)
            ? "https://kick.com"
            : _defaults.ClipsWebApiBaseUrl.TrimEnd('/');

        var json = await _fetcher.FetchAsync($"{baseUrl}/api/v2/channels/{Uri.EscapeDataString(slug)}", ct);
        if (json is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out _))
                return null;
            return Parse(root, slug, json);
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "Failed to parse channel info for {Slug}", slug);
            return null;
        }
    }

    private static KickChannelInfo Parse(JsonElement root, string fallbackSlug, string rawJson)
    {
        var slug = root.ReadAsString("slug");
        if (string.IsNullOrEmpty(slug)) slug = fallbackSlug;

        // Kick's `verified`/`is_banned` are either an object (when true) or null/absent.
        var verified = root.TryGetProperty("verified", out var v) && (v.ValueKind is JsonValueKind.True or JsonValueKind.Object);
        var isBanned = root.TryGetProperty("is_banned", out var bn) && (bn.ValueKind is JsonValueKind.True or JsonValueKind.Object);

        string username = slug;
        string? profilePic = null;
        if (root.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object)
        {
            var un = u.ReadAsString("username");
            if (!string.IsNullOrEmpty(un)) username = un;
            profilePic = NullIfEmpty(u.ReadAsString("profile_pic"));
        }

        var isLive = false;
        var viewers = 0;
        string? title = null, lang = null, thumb = null;
        DateTime? started = null;
        var mature = false;
        KickChannelCategory? category = null;

        if (root.TryGetProperty("livestream", out var ls) && ls.ValueKind == JsonValueKind.Object)
        {
            isLive = ls.ReadAsBool("is_live");
            viewers = ls.ReadAsInt("viewer_count");
            title = NullIfEmpty(ls.ReadAsString("session_title"));
            started = ParseDate(ls, "start_time") ?? ParseDate(ls, "created_at");
            lang = NullIfEmpty(ls.ReadAsString("language"));
            mature = ls.ReadAsBool("is_mature");
            thumb = UrlOf(ls, "thumbnail");
            if (ls.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array && cats.GetArrayLength() > 0)
                category = ParseCategory(cats[0]);
        }

        // Offline: fall back to the most recent category so subscribers still get context.
        if (category is null && root.TryGetProperty("recent_categories", out var rc) && rc.ValueKind == JsonValueKind.Array && rc.GetArrayLength() > 0)
            category = ParseCategory(rc[0]);

        return new KickChannelInfo(
            slug,
            root.ReadAsString("id"),
            root.ReadAsString("user_id"),
            username,
            root.ReadAsLong("followers_count"),
            verified,
            isBanned,
            root.ReadAsBool("vod_enabled"),
            root.ReadAsBool("subscription_enabled"),
            root.ReadAsBool("is_affiliate"),
            profilePic,
            UrlOf(root, "banner_image"),
            NullIfEmpty(root.ReadAsString("playback_url")),
            isLive,
            viewers,
            title,
            started,
            lang,
            mature,
            thumb,
            category,
            rawJson);
    }

    private static KickChannelCategory ParseCategory(JsonElement c) =>
        new(c.ReadAsString("id"), c.ReadAsString("name"), c.ReadAsString("slug"), c.ReadAsInt("viewers"));

    /// <summary>Resolves an image field that Kick returns as either a string or a <c>{ "url": … }</c> object.</summary>
    private static string? UrlOf(JsonElement parent, string prop)
    {
        if (!parent.TryGetProperty(prop, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => NullIfEmpty(el.GetString() ?? ""),
            JsonValueKind.Object => NullIfEmpty(el.ReadAsString("url")),
            _ => null
        };
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
