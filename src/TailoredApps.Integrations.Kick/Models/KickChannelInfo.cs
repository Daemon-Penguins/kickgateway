namespace TailoredApps.Integrations.Kick.Models;

/// <summary>
/// A channel's live info/stats as returned by the website API
/// (<c>kick.com/api/v2/channels/{slug}</c>). Not part of the official public API;
/// read via the sidecar (see <see cref="Channels.IKickChannelClient"/>).
/// <see cref="RawJson"/> is the full upstream payload for fields not surfaced here.
/// </summary>
public record KickChannelInfo(
    string Slug,
    string ChannelId,
    string UserId,
    string Username,
    long FollowersCount,
    bool Verified,
    bool IsBanned,
    bool VodEnabled,
    bool SubscriptionEnabled,
    bool IsAffiliate,
    string? ProfilePicUrl,
    string? BannerImageUrl,
    string? PlaybackUrl,
    bool IsLive,
    int ViewerCount,
    string? StreamTitle,
    DateTime? StreamStartedAt,
    string? Language,
    bool IsMature,
    string? ThumbnailUrl,
    KickChannelCategory? Category,
    string RawJson);

/// <summary>Category (game/section) a channel is streaming under.</summary>
public record KickChannelCategory(string Id, string Name, string Slug, int Viewers);
