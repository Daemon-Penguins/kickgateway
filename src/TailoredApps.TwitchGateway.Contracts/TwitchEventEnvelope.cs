namespace TailoredApps.TwitchGateway.Contracts;

/// <summary>
/// Common envelope every Twitch contract carries. The family is sourced from Twitch's official
/// EventSub WebSocket transport (chat) and Helix polling (stream state), ingested by
/// <c>TailoredApps.TwitchGateway.Listener</c> through its own inbox + outbox, and published on
/// its own topic exchanges routed by <see cref="BroadcasterLogin"/>. It never shares an exchange
/// with the Kick families, so a Twitch login that happens to equal a Kick slug cannot collide.
/// </summary>
public interface ITwitchEvent
{
    /// <summary>Broadcaster's Twitch login (lowercase, as in twitch.tv/&lt;login&gt;) - the routing key.</summary>
    string BroadcasterLogin { get; }

    /// <summary>Broadcaster's numeric Twitch user id.</summary>
    string BroadcasterUserId { get; }

    /// <summary>Broadcaster's display name (may differ from the login in case/characters).</summary>
    string BroadcasterName { get; }

    /// <summary>EventSub subscription type (<c>channel.chat.message</c>) or a synthetic <c>helix.*</c> type for polled state.</summary>
    string EventType { get; }

    /// <summary>EventSub subscription version (<c>1</c>, <c>2</c>); empty for synthetic events.</summary>
    string EventVersion { get; }

    /// <summary>EventSub <c>metadata.message_id</c> (unique per notification) or a synthetic id for polled events.</summary>
    string MessageId { get; }

    /// <summary>Stable key used for inbox dedupe (<c>es|&lt;message_id&gt;</c> for EventSub, <c>helix|...</c> for polled state).</summary>
    string DedupeKey { get; }

    /// <summary>When the listener received the event (server clock, UTC).</summary>
    DateTime ReceivedAt { get; }

    /// <summary>EventSub <c>metadata.message_timestamp</c> (UTC) or the polled timestamp; null when unknown.</summary>
    DateTime? TwitchTimestamp { get; }

    /// <summary>Raw JSON of the EventSub <c>payload.event</c> object (or the Helix item) - reach here for anything the typed contract did not surface.</summary>
    string RawPayload { get; }
}

/// <summary>Base record implementing <see cref="ITwitchEvent"/>. Concrete contracts add their typed fields.</summary>
public abstract record TwitchEventBase : ITwitchEvent
{
    public string BroadcasterLogin { get; init; } = "";
    public string BroadcasterUserId { get; init; } = "";
    public string BroadcasterName { get; init; } = "";
    public string EventType { get; init; } = "";
    public string EventVersion { get; init; } = "";
    public string MessageId { get; init; } = "";
    public string DedupeKey { get; init; } = "";
    public DateTime ReceivedAt { get; init; }
    public DateTime? TwitchTimestamp { get; init; }
    public string RawPayload { get; init; } = "";
}

/// <summary>
/// Any EventSub notification the listener subscribed to but has no typed contract for (a new
/// subscription type, or a payload it could not parse). Consumers inspect <see cref="ITwitchEvent.RawPayload"/>.
/// </summary>
public record TwitchEventUnknown : TwitchEventBase;
