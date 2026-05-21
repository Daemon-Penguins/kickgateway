namespace TailoredApps.KickGateway.Contracts.Events;

public record LivestreamMetadataUpdated : KickEventBase
{
    public string? Title { get; init; }
    public string? Language { get; init; }
    public bool? HasMatureContent { get; init; }
    public string? CategoryId { get; init; }
    public string? CategoryName { get; init; }
}
