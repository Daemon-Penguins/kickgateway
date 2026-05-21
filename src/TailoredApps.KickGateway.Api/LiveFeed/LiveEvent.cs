namespace TailoredApps.KickGateway.Api.LiveFeed;

/// <summary>
/// Flattened, UI-friendly view of one webhook event for the live feed.
/// Built at consume time from a <see cref="TailoredApps.KickGateway.Contracts.IKickEvent"/>.
/// </summary>
public sealed record LiveEvent(
    Guid ClientAppId,
    Guid BroadcasterAccountId,
    string BroadcasterSlug,
    string BroadcasterUserId,
    string EventType,
    string Summary,
    DateTime ReceivedAt,
    DateTime? KickTimestamp,
    string KickMessageId,
    string RawPayload);
