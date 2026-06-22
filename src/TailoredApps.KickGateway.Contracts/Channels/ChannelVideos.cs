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
public record ChannelVideo(
    string LivestreamId,
    string VideoUuid,
    string? Title,
    DateTime? StartTimeUtc,
    long DurationMs,
    bool IsLive,
    int ViewerCount);
