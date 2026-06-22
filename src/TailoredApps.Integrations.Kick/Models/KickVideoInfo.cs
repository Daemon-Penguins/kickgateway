namespace TailoredApps.Integrations.Kick.Models;

/// <summary>
/// A single past broadcast (VOD) entry as returned by the website API
/// (<c>kick.com/api/v2/channels/{slug}/videos</c>). Not part of the official
/// public API; read via the sidecar (see <see cref="Videos.IKickVideosClient"/>).
/// </summary>
public record KickVideoInfo(
    string LivestreamId,
    string VideoUuid,
    string? Title,
    DateTime? StartTimeUtc,
    long DurationMs,
    bool IsLive,
    int ViewerCount);
