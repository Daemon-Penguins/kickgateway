namespace TailoredApps.KickGateway.Contracts.Events;

public record ModerationBanned : KickEventBase
{
    public string BannedUserId { get; init; } = "";
    public string BannedUsername { get; init; } = "";
    public string ModeratorUserId { get; init; } = "";
    public string ModeratorUsername { get; init; } = "";
    public string? Reason { get; init; }
    public DateTime? BannedAt { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public bool IsPermanent { get; init; }
}
