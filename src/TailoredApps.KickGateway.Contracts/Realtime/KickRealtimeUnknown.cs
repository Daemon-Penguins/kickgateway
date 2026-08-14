namespace TailoredApps.KickGateway.Contracts.Realtime;

/// <summary>
/// Catch-all for any Pusher frame the listener doesn't map to a typed contract.
/// The <c>App\Events\*</c> names are reverse-engineered and drift over time — routing
/// unknowns here means nothing is silently dropped and new/renamed events stay observable
/// (subscribers can bind this and inspect <see cref="IKickRealtimeEvent.RawData"/>).
/// </summary>
public record KickRealtimeUnknown : KickRealtimeEventBase;
