namespace TailoredApps.TwitchGateway.Contracts.Channel;

/// <summary>
/// The broadcaster went live. Sourced from Helix <c>GET /streams</c> polling (synthetic
/// <c>helix.stream.online</c>), not EventSub: on the WebSocket transport <c>stream.online</c> for a
/// channel that has not authorized the app costs 1 of a 10-point budget, which would cap the roster at
/// a handful of channels. Polling every ~30 s covers hundreds of channels in one request.
/// </summary>
public record TwitchStreamOnline : TwitchEventBase
{
    /// <summary>Helix stream id - stable for the whole broadcast; the audio capture tags its segments with it.</summary>
    public string StreamId { get; init; } = "";

    /// <summary><c>live</c>, <c>playlist</c>, <c>watch_party</c>, <c>premiere</c>, <c>rerun</c>.</summary>
    public string StreamType { get; init; } = "live";

    public DateTime StartedAt { get; init; }
    public string Title { get; init; } = "";
    public string CategoryId { get; init; } = "";
    public string CategoryName { get; init; } = "";
    public string Language { get; init; } = "";
    public int ViewerCount { get; init; }
}

/// <summary>The broadcaster went offline (Helix polling; <c>helix.stream.offline</c>). <see cref="StreamId"/> is the broadcast that ended.</summary>
public record TwitchStreamOffline : TwitchEventBase
{
    public string StreamId { get; init; } = "";
    public DateTime? StartedAt { get; init; }
    public DateTime EndedAt { get; init; }
}

/// <summary>Title / category / language changed while live (Helix polling; <c>helix.channel.update</c>).</summary>
public record TwitchChannelUpdated : TwitchEventBase
{
    public string StreamId { get; init; } = "";
    public string Title { get; init; } = "";
    public string CategoryId { get; init; } = "";
    public string CategoryName { get; init; } = "";
    public string Language { get; init; } = "";
    public int ViewerCount { get; init; }
}
