namespace TailoredApps.KickGateway.Contracts;

/// <summary>
/// Common envelope fields every Kick event contract carries. Subscribers can
/// use <see cref="KickMessageId"/> to dedupe on their side and
/// <see cref="BroadcasterAccountId"/> to fan out per broadcaster.
/// </summary>
public interface IKickEvent
{
    /// <summary>Kick-Event-Message-Id from the webhook headers — unique per delivery, used for dedupe.</summary>
    string KickMessageId { get; }

    /// <summary>Kick-Event-Subscription-Id — identifies which subscription delivered the event.</summary>
    string KickSubscriptionId { get; }

    /// <summary>Internal id (GUID) of the broadcaster row this delivery is bound to.</summary>
    Guid BroadcasterAccountId { get; }

    /// <summary>Kick numeric user id of the broadcaster.</summary>
    string BroadcasterUserId { get; }

    /// <summary>Channel slug of the broadcaster (lowercase).</summary>
    string BroadcasterSlug { get; }

    /// <summary>When the webhook hit the gateway (server clock, UTC).</summary>
    DateTime ReceivedAt { get; }

    /// <summary>Kick-Event-Message-Timestamp parsed to UTC. May be null if Kick sent a non-RFC3339 value.</summary>
    DateTime? KickTimestamp { get; }

    /// <summary>Raw JSON body Kick sent. Lets subscribers reach for fields the typed contract didn't surface.</summary>
    string RawPayload { get; }
}

public abstract record KickEventBase : IKickEvent
{
    public string KickMessageId { get; init; } = "";
    public string KickSubscriptionId { get; init; } = "";
    public Guid BroadcasterAccountId { get; init; }
    public string BroadcasterUserId { get; init; } = "";
    public string BroadcasterSlug { get; init; } = "";
    public DateTime ReceivedAt { get; init; }
    public DateTime? KickTimestamp { get; init; }
    public string RawPayload { get; init; } = "";
}
