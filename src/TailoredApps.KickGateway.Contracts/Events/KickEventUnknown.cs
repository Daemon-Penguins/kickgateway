namespace TailoredApps.KickGateway.Contracts.Events;

/// <summary>
/// Fallback published when Kick delivers an event whose Kick-Event-Type doesn't
/// map to a typed contract. Subscribers that want the raw firehose can bind to
/// this and inspect <see cref="EventType"/> + <see cref="KickEventBase.RawPayload"/>.
/// </summary>
public record KickEventUnknown : KickEventBase
{
    public string EventType { get; init; } = "";
    public string EventVersion { get; init; } = "";
}
