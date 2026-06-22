namespace TailoredApps.KickGateway.Contracts.Channels;

/// <summary>
/// Ask the gateway to fetch a channel's past broadcasts (VODs) and publish a
/// <see cref="ChannelVideos"/> in response. Routed by <see cref="BroadcasterSlug"/>.
///
/// Two ways to use it:
///  * <b>Publish</b> it — the gateway binds every slug and publishes a
///    <see cref="ChannelVideos"/> back (subscribe by slug).
///  * <b>Request</b> it via <c>IRequestClient&lt;ChannelVideosRequested&gt;</c> — the
///    consumer also responds with <see cref="ChannelVideos"/>.
/// </summary>
public record ChannelVideosRequested
{
    /// <summary>Channel slug to fetch (lowercase). Required.</summary>
    public string BroadcasterSlug { get; init; } = "";

    /// <summary>Optional internal broadcaster id — echoed back on the response for correlation.</summary>
    public Guid? BroadcasterAccountId { get; init; }

    /// <summary>When the request was made (UTC). Optional, informational.</summary>
    public DateTime? RequestedAt { get; init; }
}
