namespace TailoredApps.KickGateway.Contracts.Events;

public record ChatMessageSent : KickEventBase
{
    public string MessageId { get; init; } = "";
    public string Content { get; init; } = "";
    public DateTime CreatedAt { get; init; }
    public string SenderUserId { get; init; } = "";
    public string SenderUsername { get; init; } = "";
    public string? SenderIdentityColor { get; init; }
    public string[] SenderBadges { get; init; } = Array.Empty<string>();
}
