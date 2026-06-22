using MassTransit;
using TailoredApps.Integrations.Kick.Models;
using TailoredApps.Integrations.Kick.Videos;
using TailoredApps.KickGateway.Contracts.Channels;

namespace TailoredApps.KickGateway.Api.Channels;

/// <summary>
/// Handles <see cref="ChannelVideosRequested"/>: fetches the channel's past
/// broadcasts (VODs) from the Cloudflare-protected website API via the sidecar
/// and publishes a <see cref="ChannelVideos"/>. Also responds directly when the
/// message arrived as a MassTransit request (IRequestClient).
///
/// Always emits a <see cref="ChannelVideos"/> — even on failure (Success = false) —
/// so request/response callers never hang.
/// </summary>
public class ChannelVideosConsumer : IConsumer<ChannelVideosRequested>
{
    private readonly IKickVideosClient _videos;
    private readonly ILogger<ChannelVideosConsumer> _log;

    public ChannelVideosConsumer(IKickVideosClient videos, ILogger<ChannelVideosConsumer> log)
    {
        _videos = videos;
        _log = log;
    }

    public async Task Consume(ConsumeContext<ChannelVideosRequested> context)
    {
        var slug = (context.Message.BroadcasterSlug ?? "").Trim().ToLowerInvariant();
        var result = await BuildAsync(slug, context.Message.BroadcasterAccountId, context.CancellationToken);

        await context.Publish(result);
        if (context.RequestId is not null)
            await context.RespondAsync(result);
    }

    private async Task<ChannelVideos> BuildAsync(string slug, Guid? accountId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(slug))
            return Failed("", accountId, "missing slug");

        IReadOnlyList<KickVideoInfo> videos;
        try
        {
            videos = await _videos.GetVideosAsync(slug, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Channel videos fetch threw for {Slug}", slug);
            return Failed(slug, accountId, ex.Message);
        }

        _log.LogInformation("Channel videos {Slug}: {Count} VOD(s)", slug, videos.Count);

        return new ChannelVideos
        {
            BroadcasterSlug = slug,
            BroadcasterAccountId = accountId,
            FetchedAt = DateTime.UtcNow,
            Success = true,
            Videos = videos
                .Select(v => new ChannelVideo(
                    v.LivestreamId, v.VideoUuid, v.Title, v.StartTimeUtc, v.DurationMs, v.IsLive, v.ViewerCount))
                .ToList(),
        };
    }

    private static ChannelVideos Failed(string slug, Guid? accountId, string error) => new()
    {
        BroadcasterSlug = slug,
        BroadcasterAccountId = accountId,
        FetchedAt = DateTime.UtcNow,
        Success = false,
        Error = error,
    };
}
