namespace TailoredApps.KickGateway.Contracts.Events;

public record KicksGifted : KickEventBase
{
    public string GifterUserId { get; init; } = "";
    public string GifterUsername { get; init; } = "";
    public int Amount { get; init; }
    public string? Message { get; init; }
    public DateTime? CreatedAt { get; init; }
}
