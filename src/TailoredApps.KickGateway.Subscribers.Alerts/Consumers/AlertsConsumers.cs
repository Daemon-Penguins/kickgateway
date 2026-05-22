using MassTransit;
using TailoredApps.KickGateway.Contracts.Events;

namespace TailoredApps.KickGateway.Subscribers.Alerts.Consumers;

public sealed class AlertsLivestreamStatusConsumer(ILogger<AlertsLivestreamStatusConsumer> log) : IConsumer<LivestreamStatusUpdated>
{
    public Task Consume(ConsumeContext<LivestreamStatusUpdated> ctx)
    {
        log.LogInformation("[alerts] live={Live} {Channel} title={Title}",
            ctx.Message.IsLive, ctx.Message.BroadcasterSlug, ctx.Message.Title);
        return Task.CompletedTask;
    }
}

public sealed class AlertsModerationBannedConsumer(ILogger<AlertsModerationBannedConsumer> log) : IConsumer<ModerationBanned>
{
    public Task Consume(ConsumeContext<ModerationBanned> ctx)
    {
        log.LogWarning("[alerts] BAN on {Channel}: {Mod} -> {User} (perma={Perma})",
            ctx.Message.BroadcasterSlug, ctx.Message.ModeratorUsername, ctx.Message.BannedUsername, ctx.Message.IsPermanent);
        return Task.CompletedTask;
    }
}

public sealed class AlertsFollowedConsumer(ILogger<AlertsFollowedConsumer> log) : IConsumer<ChannelFollowed>
{
    public Task Consume(ConsumeContext<ChannelFollowed> ctx)
    {
        log.LogInformation("[alerts] new follower {User} on {Channel}",
            ctx.Message.FollowerUsername, ctx.Message.BroadcasterSlug);
        return Task.CompletedTask;
    }
}
