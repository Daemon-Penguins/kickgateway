namespace TailoredApps.Integrations.Kick.Models;

/// <summary>
/// A single Kick clip as returned by the website API
/// (<c>kick.com/api/v2/channels/{slug}/clips</c>). Clips are not part of the
/// official public API; see <see cref="Clips.IKickClipsClient"/> for why they go
/// through the browser-TLS sidecar.
/// </summary>
/// <param name="VideoUrl">HLS media playlist (.m3u8) on the clips CDN.</param>
public record KickClip(
    string Id,
    string Title,
    string ChannelSlug,
    string VideoUrl,
    string ThumbnailUrl,
    int DurationSeconds,
    int Views,
    DateTime CreatedAt,
    bool IsMature,
    string Privacy,
    string? CreatorUsername,
    string? CategoryName);
