using MassTransit;
using TailoredApps.Integrations.Kick.Channels;
using TailoredApps.Integrations.Kick.Models;
using TailoredApps.Integrations.Kick.Videos;
using TailoredApps.KickGateway.Contracts.Channels;

namespace TailoredApps.KickGateway.Api.Channels;

/// <summary>
/// Handles <see cref="ChannelStatsRequested"/>: fetches the channel's live stats
/// (viewer count, live state, …) from the Cloudflare-protected website API via the
/// sidecar and publishes a <see cref="ChannelStats"/>. Also responds directly when
/// the message arrived as a MassTransit request (IRequestClient).
///
/// Always emits a <see cref="ChannelStats"/> — even on failure (Success = false) —
/// so request/response callers never hang.
/// </summary>
public class ChannelStatsConsumer : IConsumer<ChannelStatsRequested>
{
    /// <summary>Bounds the extra sidecar round trips for the live VOD id so a stats snapshot never hangs on them.</summary>
    private static readonly TimeSpan LiveVodLookupTimeout = TimeSpan.FromSeconds(10);

    private readonly IKickChannelClient _channels;
    private readonly IKickVideosClient _videos;
    private readonly ILogger<ChannelStatsConsumer> _log;

    public ChannelStatsConsumer(IKickChannelClient channels, IKickVideosClient videos, ILogger<ChannelStatsConsumer> log)
    {
        _channels = channels;
        _videos = videos;
        _log = log;
    }

    public async Task Consume(ConsumeContext<ChannelStatsRequested> context)
    {
        var slug = (context.Message.BroadcasterSlug ?? "").Trim().ToLowerInvariant();
        var stats = await BuildAsync(slug, context.Message.BroadcasterAccountId, context.CancellationToken);

        await context.Publish(stats);
        if (context.RequestId is not null)
            await context.RespondAsync(stats);
    }

    private async Task<ChannelStats> BuildAsync(string slug, Guid? accountId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(slug))
            return Failed("", accountId, "missing slug");

        KickChannelInfo? info;
        try
        {
            info = await _channels.GetChannelAsync(slug, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Channel stats fetch threw for {Slug}", slug);
            return Failed(slug, accountId, ex.Message);
        }

        if (info is null)
            return Failed(slug, accountId, "channel not found or fetch failed");

        _log.LogInformation("Channel stats {Slug}: live={Live} viewers={Viewers}", slug, info.IsLive, info.ViewerCount);

        // The VOD of the stream in progress: subscribers that react to "went live" want a link they can
        // store right away, and the channel payload alone cannot provide it.
        var liveVodId = info.IsLive && !string.IsNullOrEmpty(info.LivestreamId)
            ? await ResolveLiveVodIdAsync(slug, info.LivestreamId, ct)
            : null;

        return new ChannelStats
        {
            BroadcasterSlug = info.Slug,
            ChannelId = info.ChannelId,
            BroadcasterUserId = info.UserId,
            BroadcasterAccountId = accountId,
            FetchedAt = DateTime.UtcNow,
            Success = true,
            Username = info.Username,
            FollowersCount = info.FollowersCount,
            Verified = info.Verified,
            IsBanned = info.IsBanned,
            VodEnabled = info.VodEnabled,
            SubscriptionEnabled = info.SubscriptionEnabled,
            IsAffiliate = info.IsAffiliate,
            ProfilePicUrl = info.ProfilePicUrl,
            BannerImageUrl = info.BannerImageUrl,
            PlaybackUrl = info.PlaybackUrl,
            IsLive = info.IsLive,
            ViewerCount = info.ViewerCount,
            StreamTitle = info.StreamTitle,
            StreamStartedAt = info.StreamStartedAt,
            Language = info.Language,
            IsMature = info.IsMature,
            ThumbnailUrl = info.ThumbnailUrl,
            LivestreamId = info.LivestreamId,
            LiveVodId = liveVodId,
            LiveVodUrl = KickWatchUrls.Vod(slug, liveVodId),
            Category = info.Category is null
                ? null
                : new ChannelStatsCategory(info.Category.Id, info.Category.Name, info.Category.Slug, info.Category.Viewers),
            RawPayload = info.RawJson,
        };
    }

    private async Task<string?> ResolveLiveVodIdAsync(string slug, string livestreamId, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(LiveVodLookupTimeout);
            return await _videos.GetLiveVodIdAsync(slug, livestreamId, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.LogDebug("Live vod_id lookup for {Slug} timed out", slug);
            return null;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Live vod_id lookup for {Slug} failed", slug);
            return null;
        }
    }

    private static ChannelStats Failed(string slug, Guid? accountId, string error) => new()
    {
        BroadcasterSlug = slug,
        BroadcasterAccountId = accountId,
        FetchedAt = DateTime.UtcNow,
        Success = false,
        Error = error,
    };
}
