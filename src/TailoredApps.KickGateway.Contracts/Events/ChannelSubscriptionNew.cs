namespace TailoredApps.KickGateway.Contracts.Events;

public record ChannelSubscriptionNew : KickEventBase
{
    public string SubscriberUserId { get; init; } = "";
    public string SubscriberUsername { get; init; } = "";
    public int Duration { get; init; }
    public DateTime? CreatedAt { get; init; }
}
