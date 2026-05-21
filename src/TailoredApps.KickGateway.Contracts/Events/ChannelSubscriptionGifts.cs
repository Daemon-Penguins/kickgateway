namespace TailoredApps.KickGateway.Contracts.Events;

public record ChannelSubscriptionGifts : KickEventBase
{
    public string GifterUserId { get; init; } = "";
    public string GifterUsername { get; init; } = "";
    public int GiftCount { get; init; }
    public int Tier { get; init; }
    public string[] RecipientUserIds { get; init; } = Array.Empty<string>();
    public string[] RecipientUsernames { get; init; } = Array.Empty<string>();
    public DateTime? CreatedAt { get; init; }
}
