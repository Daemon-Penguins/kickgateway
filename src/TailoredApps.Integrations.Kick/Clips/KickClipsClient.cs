using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TailoredApps.Integrations.Kick.Internal;
using TailoredApps.Integrations.Kick.Models;
using TailoredApps.Integrations.Kick.Sidecar;

namespace TailoredApps.Integrations.Kick.Clips;

public class KickClipsClient : IKickClipsClient
{
    private readonly IKickSidecarFetcher _fetcher;
    private readonly KickGlobalDefaults _defaults;
    private readonly ILogger<KickClipsClient> _log;

    public KickClipsClient(IKickSidecarFetcher fetcher, IOptions<KickGlobalDefaults> defaults, ILogger<KickClipsClient> log)
    {
        _fetcher = fetcher;
        _defaults = defaults.Value;
        _log = log;
    }

    public async Task<IReadOnlyList<KickClip>> GetChannelClipsAsync(string slug, int maxPages, string sort = "date", string? time = null, CancellationToken ct = default)
    {
        slug = (slug ?? "").Trim().ToLowerInvariant();
        if (slug.Length == 0) return Array.Empty<KickClip>();

        var sortParam = string.IsNullOrWhiteSpace(sort) ? "date" : sort.Trim();

        var all = new List<KickClip>();
        var seenCursors = new HashSet<string>();
        string? cursor = null;

        for (var page = 0; page < Math.Max(1, maxPages); page++)
        {
            var url = $"{WebApiBase}/api/v2/channels/{Uri.EscapeDataString(slug)}/clips?sort={Uri.EscapeDataString(sortParam)}";
            if (!string.IsNullOrWhiteSpace(time))
                url += "&time=" + Uri.EscapeDataString(time);
            if (!string.IsNullOrEmpty(cursor))
                url += "&cursor=" + Uri.EscapeDataString(cursor);

            var json = await _fetcher.FetchAsync(url, ct);
            if (json is null) break;

            string? next;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("clips", out var clipsEl) || clipsEl.ValueKind != JsonValueKind.Array)
                    break;

                var before = all.Count;
                foreach (var el in clipsEl.EnumerateArray())
                    all.Add(ParseClip(el, slug));
                if (all.Count == before) break;   // empty page → stop

                next = root.ReadAsString("nextCursor");
            }
            catch (JsonException ex)
            {
                _log.LogWarning(ex, "Failed to parse clips page for {Slug}", slug);
                break;
            }

            // nextCursor is an opaque string token; stop when it's empty or repeats.
            if (string.IsNullOrEmpty(next) || !seenCursors.Add(next)) break;
            cursor = next;
        }

        return ApplyFilters(all);
    }

    public async Task<KickClip?> GetClipAsync(string clipId, CancellationToken ct = default)
    {
        clipId = (clipId ?? "").Trim();
        if (clipId.Length == 0) return null;

        var json = await _fetcher.FetchAsync($"{WebApiBase}/api/v2/clips/{Uri.EscapeDataString(clipId)}", ct);
        if (json is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // Single-clip endpoint wraps the clip under a "clip" property.
            var el = root.TryGetProperty("clip", out var c) && c.ValueKind == JsonValueKind.Object ? c : root;
            var parsed = ParseClip(el, "");
            return string.IsNullOrEmpty(parsed.Id) ? null : parsed;
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "Failed to parse single clip {ClipId}", clipId);
            return null;
        }
    }

    private string WebApiBase => string.IsNullOrWhiteSpace(_defaults.ClipsWebApiBaseUrl)
        ? "https://kick.com"
        : _defaults.ClipsWebApiBaseUrl.TrimEnd('/');

    private IReadOnlyList<KickClip> ApplyFilters(List<KickClip> clips)
    {
        IEnumerable<KickClip> q = clips.Where(c => !string.IsNullOrEmpty(c.Id) && !string.IsNullOrEmpty(c.VideoUrl));

        if (_defaults.ClipsExcludeMature)
            q = q.Where(c => !c.IsMature);
        if (_defaults.ClipsMinDurationSeconds > 0)
            q = q.Where(c => c.DurationSeconds <= 0 || c.DurationSeconds >= _defaults.ClipsMinDurationSeconds);

        // Only public clips are playable without auth.
        q = q.Where(c => string.IsNullOrEmpty(c.Privacy) || c.Privacy.Equals("public", StringComparison.OrdinalIgnoreCase));

        // De-dupe by id while preserving the API's newest-first order.
        var seen = new HashSet<string>();
        return q.Where(c => seen.Add(c.Id)).ToList();
    }

    private static KickClip ParseClip(JsonElement e, string fallbackSlug)
    {
        var id = e.ReadAsString("id");
        var title = e.ReadAsString("title");
        var video = e.ReadAsString("video_url");
        if (string.IsNullOrEmpty(video)) video = e.ReadAsString("clip_url");
        var thumb = e.ReadAsString("thumbnail_url");
        var duration = e.ReadAsInt("duration");
        var views = e.TryGetProperty("view_count", out _) ? e.ReadAsInt("view_count") : e.ReadAsInt("views");
        var created = e.ReadAsDateTime("created_at");
        var mature = e.ReadAsBool("is_mature");
        var privacy = e.ReadAsString("privacy", "public");

        var slug = fallbackSlug;
        string? creator = null, category = null;
        if (e.TryGetProperty("channel", out var ch) && ch.ValueKind == JsonValueKind.Object)
        {
            var s = ch.ReadAsString("slug");
            if (!string.IsNullOrEmpty(s)) slug = s.ToLowerInvariant();
        }
        if (e.TryGetProperty("creator", out var cr) && cr.ValueKind == JsonValueKind.Object)
            creator = NullIfEmpty(cr.ReadAsString("username"));
        if (e.TryGetProperty("category", out var ca) && ca.ValueKind == JsonValueKind.Object)
            category = NullIfEmpty(ca.ReadAsString("name"));

        return new KickClip(id, title, slug, video, thumb, duration, views, created, mature, privacy, creator, category);
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
