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
    /// Clips for a channel slug, paginated up to <paramref name="maxPages"/> pages.
    /// <paramref name="sort"/> is the Kick sort key (<c>date</c> = newest first,
    /// <c>view</c> = most viewed); <paramref name="time"/> is the Kick time window for
    /// <c>view</c> (<c>all</c>/<c>day</c>/<c>week</c>/<c>month</c>, null for date).
    /// Returns an empty list (never throws) on no clips / fetch failure.
    /// </summary>
    Task<IReadOnlyList<KickClip>> GetChannelClipsAsync(string slug, int maxPages, string sort = "date", string? time = null, CancellationToken ct = default);

    /// <summary>
    /// A single clip by id — used to recover a fresh <c>video_url</c> if the cached pool
    /// expired mid-playback. Null when not found / fetch failed.
    /// </summary>
    Task<KickClip?> GetClipAsync(string clipId, CancellationToken ct = default);
}
