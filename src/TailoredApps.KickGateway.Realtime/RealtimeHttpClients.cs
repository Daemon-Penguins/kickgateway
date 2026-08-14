namespace TailoredApps.KickGateway.Realtime;

/// <summary>Named HttpClients + host guards shared by the live-video capture loop.</summary>
public static class RealtimeHttpClients
{
    /// <summary>Client for fetching live HLS playlists + segments from Kick's video CDN.</summary>
    public const string Hls = "KickHls";

    public const string BrowserUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    /// <summary>
    /// True if the URL is an absolute https URL. Kick's live playback URL is NOT on kick.com —
    /// it's served by the streaming CDN (currently Amazon IVS, <c>*.playback.live-video.net</c>),
    /// and Kick may change it, and segments may even live on a different host than the manifest —
    /// so we don't hardcode a host allowlist. Every URL is derived from the playback URL Kick's own
    /// channel API hands us (the same trusted source that tells us the channel is live), so we
    /// require https + the caller's size cap rather than a host pin.
    /// </summary>
    public static bool IsFetchableHttps(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;
}
