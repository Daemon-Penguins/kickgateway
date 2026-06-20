using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TailoredApps.Integrations.Kick.Internal;
using TailoredApps.Integrations.Kick.Models;

namespace TailoredApps.Integrations.Kick.Clips;

public class KickClipsClient : IKickClipsClient
{
    /// <summary>Named HttpClient that talks to the clips-fetcher sidecar.</summary>
    public const string HttpClientName = "KickClipsFetcher";

    private readonly IHttpClientFactory _factory;
    private readonly KickGlobalDefaults _defaults;
    private readonly ILogger<KickClipsClient> _log;

    public KickClipsClient(IHttpClientFactory factory, IOptions<KickGlobalDefaults> defaults, ILogger<KickClipsClient> log)
    {
        _factory = factory;
        _defaults = defaults.Value;
        _log = log;
    }

    public async Task<IReadOnlyList<KickClip>> GetChannelClipsAsync(string slug, int maxPages, CancellationToken ct = default)
    {
        slug = (slug ?? "").Trim().ToLowerInvariant();
        if (slug.Length == 0) return Array.Empty<KickClip>();

        var all = new List<KickClip>();
        var seenCursors = new HashSet<string>();
        string? cursor = null;

        for (var page = 0; page < Math.Max(1, maxPages); page++)
        {
            var url = $"{WebApiBase}/api/v2/channels/{Uri.EscapeDataString(slug)}/clips?sort=date";
            if (!string.IsNullOrEmpty(cursor))
                url += "&cursor=" + Uri.EscapeDataString(cursor);

            var json = await FetchAsync(url, ct);
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

        var json = await FetchAsync($"{WebApiBase}/api/v2/clips/{Uri.EscapeDataString(clipId)}", ct);
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

    /// <summary>GETs a Kick URL via the sidecar and returns the body, or null on any failure.</summary>
    private async Task<string?> FetchAsync(string kickUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_defaults.ClipsFetcherUrl))
        {
            _log.LogError("Kick clips fetcher not configured (Kick:ClipsFetcherUrl is empty) — cannot read clips");
            return null;
        }

        var fetchUrl = $"{_defaults.ClipsFetcherUrl.TrimEnd('/')}/fetch?url={Uri.EscapeDataString(kickUrl)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, fetchUrl);
        if (!string.IsNullOrEmpty(_defaults.ClipsFetcherSecret))
            req.Headers.TryAddWithoutValidation("X-Fetch-Secret", _defaults.ClipsFetcherSecret);

        try
        {
            var http = _factory.CreateClient(HttpClientName);
            using var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("Clips fetch failed ({Status}) for {Url}: {Body}",
                    (int)resp.StatusCode, kickUrl, Truncate(body, 300));
                return null;
            }
            return body;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Clips fetch threw for {Url}", kickUrl);
            return null;
        }
    }

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

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
