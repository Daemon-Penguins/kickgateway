namespace TailoredApps.KickGateway.Contracts.Events;

public record ChannelSubscriptionRenewal : KickEventBase
{
    public string SubscriberUserId { get; init; } = "";
    public string SubscriberUsername { get; init; } = "";
    public int Duration { get; init; }
    public int CumulativeMonths { get; init; }
    public DateTime? CreatedAt { get; init; }
}
