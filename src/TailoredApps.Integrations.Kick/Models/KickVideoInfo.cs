namespace TailoredApps.Integrations.Kick.Models;

/// <summary>
/// A single past broadcast (VOD) entry as returned by the website API
/// (<c>kick.com/api/v2/channels/{slug}/videos</c>). Not part of the official
/// public API; read via the sidecar (see <see cref="Videos.IKickVideosClient"/>).
/// </summary>
/// <param name="LivestreamId">Kick's numeric livestream id (the entry's <c>id</c>).</param>
/// <param name="VideoUuid">
/// Legacy video uuid (<c>video.uuid</c>). Still the key for
/// <c>kick.com/api/v1/video/{uuid}</c>, but NO LONGER usable in a public VOD
/// URL — use <paramref name="VodId"/> for that.
/// </param>
/// <param name="VodId">
/// The id kick.com puts in the watch URL: <c>kick.com/{slug}/videos/{VodId}</c>.
/// A time-ordered (v7) uuid Kick mints at broadcast start, exposed only as
/// <c>livestream.vod_id</c> on the per-video endpoint — the channel listing
/// never carries it. Null when the lookup failed or the broadcast has no video.
/// </param>
public record KickVideoInfo(
    string LivestreamId,
    string VideoUuid,
    string? Title,
    DateTime? StartTimeUtc,
    long DurationMs,
    bool IsLive,
    int ViewerCount,
    string? VodId = null);
