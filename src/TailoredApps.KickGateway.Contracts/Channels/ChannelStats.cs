namespace TailoredApps.KickGateway.Contracts.Channels;

/// <summary>
/// A point-in-time snapshot of a Kick channel's statistics, fetched on demand from
/// the (Cloudflare-protected) website API in response to a
/// <see cref="ChannelStatsRequested"/>. Routed by <see cref="BroadcasterSlug"/>.
///
/// Strongly-typed fields cover the commonly-useful data; <see cref="RawPayload"/>
/// carries the full upstream JSON so subscribers can reach for anything not
/// surfaced here without a contract change.
/// </summary>
public record ChannelStats
{
    // --- identity / meta ---

    /// <summary>Channel slug (lowercase) — also the routing key.</summary>
    public string BroadcasterSlug { get; init; } = "";

    /// <summary>Kick numeric channel id.</summary>
    public string ChannelId { get; init; } = "";

    /// <summary>Kick numeric user id of the broadcaster.</summary>
    public string BroadcasterUserId { get; init; } = "";

    /// <summary>Internal broadcaster id, echoed from the request when supplied.</summary>
    public Guid? BroadcasterAccountId { get; init; }

    /// <summary>When the gateway fetched this snapshot (UTC).</summary>
    public DateTime FetchedAt { get; init; }

    /// <summary>False if the upstream fetch failed (then most fields are defaults and <see cref="Error"/> is set).</summary>
    public bool Success { get; init; }

    /// <summary>Failure reason when <see cref="Success"/> is false.</summary>
    public string? Error { get; init; }

    // --- channel ---

    public string Username { get; init; } = "";
    public long FollowersCount { get; init; }
    public bool Verified { get; init; }
    public bool IsBanned { get; init; }
    public bool VodEnabled { get; init; }
    public bool SubscriptionEnabled { get; init; }
    public bool IsAffiliate { get; init; }
    public string? ProfilePicUrl { get; init; }
    public string? BannerImageUrl { get; init; }
    public string? PlaybackUrl { get; init; }

    // --- live state ---

    /// <summary>True if the channel is currently live.</summary>
    public bool IsLive { get; init; }

    /// <summary>Current concurrent viewers (0 when offline).</summary>
    public int ViewerCount { get; init; }

    public string? StreamTitle { get; init; }
    public DateTime? StreamStartedAt { get; init; }
    public string? Language { get; init; }
    public bool IsMature { get; init; }
    public string? ThumbnailUrl { get; init; }

    /// <summary>Category the channel is currently streaming under (null when offline / unknown).</summary>
    public ChannelStatsCategory? Category { get; init; }

    /// <summary>Full upstream JSON (<c>kick.com/api/v2/channels/{slug}</c>) for fields not surfaced above.</summary>
    public string RawPayload { get; init; } = "";
}

/// <summary>The category (game/section) a channel is streaming under.</summary>
public record ChannelStatsCategory(string Id, string Name, string Slug, int Viewers);
