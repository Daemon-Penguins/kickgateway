using TailoredApps.Integrations.Kick.Models;

namespace TailoredApps.Integrations.Kick.Clips;

/// <summary>
/// Reads Kick clips. Clips only exist on the Cloudflare-protected website API
/// (<c>kick.com/api/v2/...</c>), which JA3-blocks a plain .NET HttpClient, so every
/// request is routed through the browser-TLS "clips-fetcher" sidecar. All Kick
/// URL/JSON knowledge lives in the implementation; the sidecar is a dumb fetch proxy.
/// </summary>
public interface IKickClipsClient
{
    /// <summary>
    /// Newest-first clips for a channel slug, paginated up to <paramref name="maxPages"/>
    /// pages. Returns an empty list (never throws) when the channel has no clips or the
    /// fetch fails — callers treat that as "nothing to show".
    /// </summary>
    Task<IReadOnlyList<KickClip>> GetChannelClipsAsync(string slug, int maxPages, CancellationToken ct = default);

    /// <summary>
    /// A single clip by id — used to recover a fresh <c>video_url</c> if the cached pool
    /// expired mid-playback. Null when not found / fetch failed.
    /// </summary>
    Task<KickClip?> GetClipAsync(string clipId, CancellationToken ct = default);
}
