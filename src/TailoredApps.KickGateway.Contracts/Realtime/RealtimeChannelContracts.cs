namespace TailoredApps.KickGateway.Contracts.Realtime;

// Contracts sourced from the `channel.{id}` Pusher channel.

/// <summary><c>App\Events\SubscriptionEvent</c> — a channel subscription (new or renewal).</summary>
public record RealtimeSubscription : KickRealtimeEventBase
{
    public string Username { get; init; } = "";
    public int Months { get; init; }
}

/// <summary><c>App\Events\GiftedSubscriptionsEvent</c> — a bulk gift-sub drop.</summary>
public record RealtimeGiftedSubscriptions : KickRealtimeEventBase
{
    public string GifterUsername { get; init; } = "";
    public string[] GiftedUsernames { get; init; } = Array.Empty<string>();
    public int Count { get; init; }
    /// <summary>Gifter's running total of gifted subs on this channel, when provided.</summary>
    public int? GifterTotal { get; init; }
}

/// <summary><c>App\Events\LuckyUsersWhoGotGiftSubscriptionsEvent</c> — recipients of a gift-sub drop.</summary>
public record RealtimeLuckyGiftRecipients : KickRealtimeEventBase
{
    public string GifterUsername { get; init; } = "";
    public string[] Usernames { get; init; } = Array.Empty<string>();
}

/// <summary><c>App\Events\StreamerIsLive</c> — the channel went live.</summary>
public record RealtimeStreamerLive : KickRealtimeEventBase
{
    public string LivestreamId { get; init; } = "";
    public string? Title { get; init; }
    public DateTime? StartedAt { get; init; }
}

/// <summary><c>App\Events\StopStreamBroadcast</c> — the channel went offline.</summary>
public record RealtimeStreamEnd : KickRealtimeEventBase
{
    public string LivestreamId { get; init; } = "";
}

/// <summary><c>App\Events\StreamHostEvent</c> — the channel is hosting/raiding another (or being hosted).</summary>
public record RealtimeStreamHost : KickRealtimeEventBase
{
    public string HostUsername { get; init; } = "";
    public int ViewerCount { get; init; }
    public string? Message { get; init; }
}

/// <summary><c>App\Events\FollowersUpdated</c> — follower count tick (and, when present, who followed/unfollowed).</summary>
public record RealtimeFollowersUpdated : KickRealtimeEventBase
{
    public long FollowersCount { get; init; }
    public string? Username { get; init; }
    /// <summary>True = followed, false = unfollowed, null = not specified (count-only tick).</summary>
    public bool? Followed { get; init; }
}

/// <summary><c>App\Events\KicksGifted</c> — Kicks (paid) gift.</summary>
public record RealtimeKicksGifted : KickRealtimeEventBase
{
    public string SenderUsername { get; init; } = "";
    public string GiftName { get; init; } = "";
    public int Amount { get; init; }
    public string? GiftId { get; init; }
    public string? Message { get; init; }
}

/// <summary><c>App\Events\RewardRedeemedEvent</c> — a channel-points reward redemption.</summary>
public record RealtimeRewardRedeemed : KickRealtimeEventBase
{
    public string RewardTitle { get; init; } = "";
    public string UserId { get; init; } = "";
    public string Username { get; init; } = "";
    public string? UserInput { get; init; }
}
