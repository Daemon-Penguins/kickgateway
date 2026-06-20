using MassTransit;
using TailoredApps.Integrations.Kick.Channels;
using TailoredApps.Integrations.Kick.Models;
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
    private readonly IKickChannelClient _channels;
    private readonly ILogger<ChannelStatsConsumer> _log;

    public ChannelStatsConsumer(IKickChannelClient channels, ILogger<ChannelStatsConsumer> log)
    {
        _channels = channels;
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
            Category = info.Category is null
                ? null
                : new ChannelStatsCategory(info.Category.Id, info.Category.Name, info.Category.Slug, info.Category.Viewers),
            RawPayload = info.RawJson,
        };
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
