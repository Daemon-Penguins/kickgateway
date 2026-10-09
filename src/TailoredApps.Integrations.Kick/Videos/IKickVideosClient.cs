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

    /// <summary>
    /// The watch-URL id (<c>livestream.vod_id</c>) of the broadcast <paramref name="livestreamId"/>
    /// that is live on <paramref name="slug"/> right now - so a stats consumer can link to the VOD of
    /// the stream in progress. Cached per livestream id (the mapping never changes); a miss is
    /// remembered for a minute because Kick creates the video entry a little after the stream
    /// starts. Null when not (yet) resolvable.
    /// </summary>
    Task<string?> GetLiveVodIdAsync(string slug, string livestreamId, CancellationToken ct = default);
}
