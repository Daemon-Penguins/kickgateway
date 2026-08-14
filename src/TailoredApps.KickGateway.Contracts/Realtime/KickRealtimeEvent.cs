namespace TailoredApps.KickGateway.Contracts.Realtime;

/// <summary>
/// Common envelope every <b>real-time</b> (Kick Pusher WebSocket) contract carries.
/// This is a separate family from the signed-webhook contracts in
/// <c>TailoredApps.KickGateway.Contracts.Events</c>: it is sourced from Kick's
/// <i>unofficial</i> realtime stream (best-effort, unsigned) and published on its own
/// topic exchanges, routed by <see cref="BroadcasterSlug"/>.
/// </summary>
public interface IKickRealtimeEvent
{
    /// <summary>Channel slug of the broadcaster (lowercase) — the routing key.</summary>
    string BroadcasterSlug { get; }

    /// <summary>Kick numeric channel id (the <c>channel.{id}</c> Pusher channel).</summary>
    string KickChannelId { get; }

    /// <summary>Kick numeric chatroom id (the <c>chatrooms.{id}.v2</c> Pusher channel). May be null.</summary>
    string? KickChatroomId { get; }

    /// <summary>The Pusher channel this frame arrived on (e.g. <c>chatrooms.123.v2</c> or <c>channel.123</c>).</summary>
    string PusherChannel { get; }

    /// <summary>Raw Pusher event name (e.g. <c>App\Events\ChatMessageEvent</c>).</summary>
    string PusherEvent { get; }

    /// <summary>Stable per-frame key used for inbox dedupe (natural id where present, else a content hash).</summary>
    string DedupeKey { get; }

    /// <summary>When the listener received the frame (server clock, UTC).</summary>
    DateTime ReceivedAt { get; }

    /// <summary>Event timestamp parsed from the payload (UTC), when the payload carries one.</summary>
    DateTime? KickTimestamp { get; }

    /// <summary>Raw inner JSON of the Pusher frame's <c>data</c> field — reach here for anything the typed contract didn't surface.</summary>
    string RawData { get; }
}

/// <summary>Base record implementing <see cref="IKickRealtimeEvent"/>. Concrete contracts add their typed fields.</summary>
public abstract record KickRealtimeEventBase : IKickRealtimeEvent
{
    public string BroadcasterSlug { get; init; } = "";
    public string KickChannelId { get; init; } = "";
    public string? KickChatroomId { get; init; }
    public string PusherChannel { get; init; } = "";
    public string PusherEvent { get; init; } = "";
    public string DedupeKey { get; init; } = "";
    public DateTime ReceivedAt { get; init; }
    public DateTime? KickTimestamp { get; init; }
    public string RawData { get; init; } = "";
}
