namespace TailoredApps.KickGateway.Contracts.Events;

public record ChannelFollowed : KickEventBase
{
    public string FollowerUserId { get; init; } = "";
    public string FollowerUsername { get; init; } = "";
}
