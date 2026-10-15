namespace TailoredApps.TwitchGateway.Contracts.Channel;

/// <summary>
/// Ask the gateway for a Twitch channel's current state: live or not, viewer count, title, category and
/// the VOD of the broadcast in progress. The Twitch counterpart of the Kick <c>ChannelStatsRequested</c>:
/// the gateway answers from Helix (<c>GET /users</c>, <c>GET /streams</c>, <c>GET /videos</c>) with its
/// own app token, so a subscriber that samples viewers (every 90 s, say) never touches the Twitch API.
/// Published to a login-routed topic exchange (<see cref="TwitchEventTopology.ConfigurePublishTopology"/>);
/// works both as plain publish (the gateway publishes a <see cref="TwitchStreamStats"/>) and as MassTransit
/// request/response (<c>IRequestClient&lt;TwitchStreamStatsRequested&gt;</c>).
/// </summary>
public record TwitchStreamStatsRequested
{
    /// <summary>The broadcaster's Twitch login (case-insensitive; normalised to lowercase).</summary>
    public string BroadcasterLogin { get; init; } = "";

    public DateTime? RequestedAt { get; init; }
}

/// <summary>
/// A snapshot of a Twitch channel. <see cref="Success"/> false = the lookup itself failed (unknown login,
/// Twitch application not configured on the gateway, Helix error) and only <see cref="Error"/> is
/// meaningful; <see cref="IsLive"/> false with <see cref="Success"/> true = the channel exists and is offline.
/// </summary>
public record TwitchStreamStats
{
    // --- identity / meta ---
    public string BroadcasterLogin { get; init; } = "";
    public string BroadcasterUserId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string? ProfileImageUrl { get; init; }

    /// <summary><c>partner</c>, <c>affiliate</c> or empty.</summary>
    public string BroadcasterType { get; init; } = "";

    public DateTime FetchedAt { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }

    // --- live state (Helix GET /streams; all null/0 while offline) ---
    public bool IsLive { get; init; }

    /// <summary>Helix stream id of the broadcast in progress - the same id the <c>TwitchStreamOnline</c> event carries.</summary>
    public string? StreamId { get; init; }

    public int ViewerCount { get; init; }
    public string? Title { get; init; }
    public string? CategoryId { get; init; }
    public string? CategoryName { get; init; }
    public string? Language { get; init; }
    public DateTime? StartedAt { get; init; }
    public bool IsMature { get; init; }

    // --- the VOD of the broadcast in progress (Helix GET /videos?type=archive, matched on stream_id) ---
    /// <summary>Twitch video id of the running broadcast's VOD, when Twitch has created it (usually within a minute of going live; needs VODs enabled on the channel).</summary>
    public string? LiveVodId { get; init; }

    /// <summary>Ready-to-use watch URL of that VOD (<c>https://www.twitch.tv/videos/{id}</c>).</summary>
    public string? LiveVodUrl { get; init; }

    /// <summary>When that VOD starts (Helix <c>created_at</c>) - the instant that is <c>t=0</c> for seek links.</summary>
    public DateTime? LiveVodStartedAt { get; init; }
}

/// <summary>Twitch watch URLs. Seek links append <c>?t=1h2m3s</c> (Twitch's player takes the h/m/s form, not plain seconds).</summary>
public static class TwitchWatchUrls
{
    public static string? Vod(string? videoId) =>
        string.IsNullOrWhiteSpace(videoId) ? null : $"https://www.twitch.tv/videos/{videoId.Trim()}";

    public static string Channel(string login) => $"https://www.twitch.tv/{login.Trim().ToLowerInvariant()}";

    /// <summary>Twitch's seek syntax for a VOD link: <c>1h2m3s</c> (hours omitted when zero).</summary>
    public static string SeekParameter(long seconds)
    {
        if (seconds < 0) seconds = 0;
        var h = seconds / 3600;
        var m = seconds % 3600 / 60;
        var s = seconds % 60;
        return h > 0 ? $"{h}h{m}m{s}s" : $"{m}m{s}s";
    }
}
