using TailoredApps.Integrations.Kick.Models;

namespace TailoredApps.Integrations.Kick.Videos;

/// <summary>
/// Reads a Kick channel's past broadcasts (VODs) from the Cloudflare-protected
/// website API (<c>kick.com/api/v2/channels/{slug}/videos</c>) via the sidecar.
/// The official <c>public/v1</c> API does not expose this listing, hence the
/// shared <see cref="Sidecar.IKickSidecarFetcher"/>.
/// </summary>
public interface IKickVideosClient
{
    /// <summary>
    /// Fetch the channel's videos by slug. Returns an empty list if the channel
    /// is unknown or the fetch failed.
    /// </summary>
    Task<IReadOnlyList<KickVideoInfo>> GetVideosAsync(string slug, CancellationToken ct = default);
}
