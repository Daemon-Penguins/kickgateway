namespace TailoredApps.KickGateway.Contracts.Events;

public record LivestreamStatusUpdated : KickEventBase
{
    public bool IsLive { get; init; }
    public string? Title { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? EndedAt { get; init; }
}
