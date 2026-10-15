using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TailoredApps.Integrations.Twitch.Playback;

public sealed class TwitchPlaybackClient : ITwitchPlaybackClient
{
    private readonly IHttpClientFactory _factory;
    private readonly TwitchOptions _o;
    private readonly ILogger<TwitchPlaybackClient> _log;

    public TwitchPlaybackClient(IHttpClientFactory factory, IOptions<TwitchOptions> options, ILogger<TwitchPlaybackClient> log)
    {
        _factory = factory;
        _o = options.Value;
        _log = log;
    }

    public async Task<TwitchPlaybackAccess?> GetLivePlaybackAccessAsync(string login, CancellationToken ct = default)
    {
        login = login.Trim().ToLowerInvariant();
        var body = JsonSerializer.Serialize(new
        {
            operationName = "PlaybackAccessToken",
            variables = new { isLive = true, login, isVod = false, vodID = "", playerType = "embed", platform = "site" },
            extensions = new { persistedQuery = new { version = 1, sha256Hash = _o.PlaybackAccessTokenHash } },
        });
        var http = _factory.CreateClient(ServiceCollectionExtensions.GqlHttpClientName);
        using var resp = await http.PostAsync(_o.GqlUrl, new StringContent(body, Encoding.UTF8, "application/json"), ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogWarning("[{Login}] GQL PlaybackAccessToken returned HTTP {Status}: {Body}", login, (int)resp.StatusCode, Truncate(raw, 200));
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
                _log.LogWarning("[{Login}] GQL PlaybackAccessToken errors: {Errors}", login, Truncate(errors.GetRawText(), 300));
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("streamPlaybackAccessToken", out var t) || t.ValueKind != JsonValueKind.Object)
                return null;
            var value = t.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var sig = t.TryGetProperty("signature", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "";
            if (value.Length == 0 || sig.Length == 0) return null;

            DateTime? expires = null;
            var channelId = "";
            var showAds = true;
            try
            {
                using var claims = JsonDocument.Parse(value);
                var c = claims.RootElement;
                if (c.TryGetProperty("expires", out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var unix))
                    expires = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
                if (c.TryGetProperty("channel_id", out var cid) && cid.ValueKind == JsonValueKind.Number) channelId = cid.GetRawText();
                if (c.TryGetProperty("show_ads", out var ads) && ads.ValueKind == JsonValueKind.False) showAds = false;
            }
            catch (JsonException) { /* opaque token - fine */ }

            return new TwitchPlaybackAccess(value, sig, DateTime.UtcNow, expires, channelId, showAds);
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "[{Login}] GQL PlaybackAccessToken: unparseable response", login);
            return null;
        }
    }

    public Uri BuildMasterPlaylistUri(string login, TwitchPlaybackAccess access)
    {
        login = login.Trim().ToLowerInvariant();
        var q = new[]
        {
            ("sig", access.Signature),
            ("token", access.Token),
            ("allow_source", "true"),
            ("allow_audio_only", "true"),
            ("playlist_include_framerate", "true"),
            ("fast_bread", "true"),
            ("platform", "web"),
            ("supported_codecs", "h264"),
            ("p", Random.Shared.Next(100_000, 999_999).ToString()),
        };
        var query = string.Join("&", q.Select(kv => $"{kv.Item1}={Uri.EscapeDataString(kv.Item2)}"));
        return new Uri($"{_o.UsherBaseUrl.TrimEnd('/')}/api/channel/hls/{Uri.EscapeDataString(login)}.m3u8?{query}");
    }

    public async Task<TwitchPlaylistResponse> GetPlaylistAsync(Uri uri, CancellationToken ct = default)
    {
        var http = _factory.CreateClient(ServiceCollectionExtensions.PlaybackHttpClientName);
        using var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseContentRead, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        return new TwitchPlaylistResponse((int)resp.StatusCode, body, resp.RequestMessage?.RequestUri ?? uri);
    }

    public async Task<byte[]?> GetSegmentAsync(Uri uri, int maxBytes, CancellationToken ct = default)
    {
        var http = _factory.CreateClient(ServiceCollectionExtensions.PlaybackHttpClientName);
        try
        {
            using var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return null;
            if (resp.Content.Headers.ContentLength is { } len && len > maxBytes) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var ms = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                ms.Write(buffer, 0, read);
                if (ms.Length > maxBytes) return null;
            }
            return ms.ToArray();
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
