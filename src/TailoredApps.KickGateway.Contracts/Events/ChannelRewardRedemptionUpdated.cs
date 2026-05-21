namespace TailoredApps.KickGateway.Contracts.Events;

public record ChannelRewardRedemptionUpdated : KickEventBase
{
    public string RedemptionId { get; init; } = "";
    public string RewardId { get; init; } = "";
    public string RewardTitle { get; init; } = "";
    public string UserId { get; init; } = "";
    public string Username { get; init; } = "";
    public string Status { get; init; } = "";
    public string? UserInput { get; init; }
    public DateTime? RedeemedAt { get; init; }
}
