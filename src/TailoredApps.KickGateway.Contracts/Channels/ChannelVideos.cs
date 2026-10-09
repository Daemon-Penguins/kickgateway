namespace TailoredApps.KickGateway.Contracts.Channels;

/// <summary>
/// A channel's list of past broadcasts (VODs), fetched on demand from the
/// (Cloudflare-protected) website API in response to a
/// <see cref="ChannelVideosRequested"/>. Routed by <see cref="BroadcasterSlug"/>.
/// </summary>
public record ChannelVideos
{
    /// <summary>Channel slug (lowercase) — also the routing key.</summary>
    public string BroadcasterSlug { get; init; } = "";

    /// <summary>Internal broadcaster id, echoed from the request when supplied.</summary>
    public Guid? BroadcasterAccountId { get; init; }

    /// <summary>When the gateway fetched this listing (UTC).</summary>
    public DateTime FetchedAt { get; init; }

    /// <summary>False if the upstream fetch failed (then <see cref="Videos"/> is empty and <see cref="Error"/> is set).</summary>
    public bool Success { get; init; }

    /// <summary>Failure reason when <see cref="Success"/> is false.</summary>
    public string? Error { get; init; }

    /// <summary>The channel's videos, newest first as returned by Kick. Empty on failure.</summary>
    public IReadOnlyList<ChannelVideo> Videos { get; init; } = [];
}

/// <summary>A single past broadcast (VOD) entry.</summary>
/// <param name="LivestreamId">Kick's numeric livestream id — the exact identity of the broadcast.</param>
/// <param name="VideoUuid">
/// Legacy video uuid. Kept for correlation, but it does NOT work in a watch
/// URL any more — build links from <paramref name="VodId"/>.
/// </param>
/// <param name="Title">Broadcast title (<c>session_title</c>), null when unset.</param>
/// <param name="StartTimeUtc">When the broadcast started (UTC), null when Kick reported none.</param>
/// <param name="DurationMs">Length in milliseconds; 0 while still live.</param>
/// <param name="IsLive">True for the broadcast Kick currently flags as live.</param>
/// <param name="ViewerCount">Viewers at the time of the fetch.</param>
/// <param name="VodId">
/// The id kick.com uses in the watch URL: <c>https://kick.com/{slug}/videos/{VodId}</c>.
/// Null when the gateway could not resolve it (no video yet, or the lookup failed);
/// a link must not be built in that case.
/// </param>
/// <param name="WatchUrl">
/// Ready-to-use watch URL, <c>https://kick.com/{slug}/videos/{VodId}</c>; null when <paramref name="VodId"/>
/// is unknown. Use this instead of building URLs yourself - <paramref name="VideoUuid"/> in a URL 404s.
/// </param>
public record ChannelVideo(
    string LivestreamId,
    string VideoUuid,
    string? Title,
    DateTime? StartTimeUtc,
    long DurationMs,
    bool IsLive,
    int ViewerCount,
    string? VodId = null,
    string? WatchUrl = null);

/// <summary>The one place that knows how kick.com watch URLs look.</summary>
public static class KickWatchUrls
{
    /// <summary><c>https://kick.com/{slug}/videos/{vodId}</c>, or null when there is no vod id to link to.</summary>
    public static string? Vod(string slug, string? vodId) =>
        string.IsNullOrWhiteSpace(vodId) || string.IsNullOrWhiteSpace(slug)
            ? null
            : $"https://kick.com/{slug.Trim().ToLowerInvariant()}/videos/{vodId.Trim()}";
}
