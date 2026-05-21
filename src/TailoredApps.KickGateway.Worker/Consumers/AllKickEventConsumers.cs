using MassTransit;
using TailoredApps.KickGateway.Contracts.Events;

namespace TailoredApps.KickGateway.Worker.Consumers;

// Sample consumers — one per Kick event. They all just log. Replace the bodies
// with your domain handling. Each consumer is bound to its own queue (named via
// the kebab-case formatter set in Program.cs) which MassTransit binds to the
// per-message-type fanout exchange. Multiple worker instances form a single
// competing-consumer group on each queue.

public class ChatMessageSentConsumer(ILogger<ChatMessageSentConsumer> log) : IConsumer<ChatMessageSent>
{
    public Task Consume(ConsumeContext<ChatMessageSent> ctx)
    {
        log.LogInformation("CHAT [{Channel}] {User}: {Text}", ctx.Message.BroadcasterSlug, ctx.Message.SenderUsername, ctx.Message.Content);
        return Task.CompletedTask;
    }
}

public class ChannelFollowedConsumer(ILogger<ChannelFollowedConsumer> log) : IConsumer<ChannelFollowed>
{
    public Task Consume(ConsumeContext<ChannelFollowed> ctx)
    {
        log.LogInformation("FOLLOW [{Channel}] {User} followed", ctx.Message.BroadcasterSlug, ctx.Message.FollowerUsername);
        return Task.CompletedTask;
    }
}

public class ChannelSubscriptionNewConsumer(ILogger<ChannelSubscriptionNewConsumer> log) : IConsumer<ChannelSubscriptionNew>
{
    public Task Consume(ConsumeContext<ChannelSubscriptionNew> ctx)
    {
        log.LogInformation("SUB [{Channel}] {User} subscribed for {Months} months", ctx.Message.BroadcasterSlug, ctx.Message.SubscriberUsername, ctx.Message.Duration);
        return Task.CompletedTask;
    }
}

public class ChannelSubscriptionGiftsConsumer(ILogger<ChannelSubscriptionGiftsConsumer> log) : IConsumer<ChannelSubscriptionGifts>
{
    public Task Consume(ConsumeContext<ChannelSubscriptionGifts> ctx)
    {
        log.LogInformation("GIFT [{Channel}] {User} gifted {Count} subs", ctx.Message.BroadcasterSlug, ctx.Message.GifterUsername, ctx.Message.GiftCount);
        return Task.CompletedTask;
    }
}

public class ChannelSubscriptionRenewalConsumer(ILogger<ChannelSubscriptionRenewalConsumer> log) : IConsumer<ChannelSubscriptionRenewal>
{
    public Task Consume(ConsumeContext<ChannelSubscriptionRenewal> ctx)
    {
        log.LogInformation("RENEW [{Channel}] {User} renewed ({CumulativeMonths} cumulative)", ctx.Message.BroadcasterSlug, ctx.Message.SubscriberUsername, ctx.Message.CumulativeMonths);
        return Task.CompletedTask;
    }
}

public class LivestreamStatusUpdatedConsumer(ILogger<LivestreamStatusUpdatedConsumer> log) : IConsumer<LivestreamStatusUpdated>
{
    public Task Consume(ConsumeContext<LivestreamStatusUpdated> ctx)
    {
        log.LogInformation("STREAM [{Channel}] live={Live} title={Title}", ctx.Message.BroadcasterSlug, ctx.Message.IsLive, ctx.Message.Title);
        return Task.CompletedTask;
    }
}

public class LivestreamMetadataUpdatedConsumer(ILogger<LivestreamMetadataUpdatedConsumer> log) : IConsumer<LivestreamMetadataUpdated>
{
    public Task Consume(ConsumeContext<LivestreamMetadataUpdated> ctx)
    {
        log.LogInformation("META [{Channel}] title={Title} category={Cat}", ctx.Message.BroadcasterSlug, ctx.Message.Title, ctx.Message.CategoryName);
        return Task.CompletedTask;
    }
}

public class ModerationBannedConsumer(ILogger<ModerationBannedConsumer> log) : IConsumer<ModerationBanned>
{
    public Task Consume(ConsumeContext<ModerationBanned> ctx)
    {
        log.LogInformation("BAN [{Channel}] {Mod} -> {User} (perma={Perma})", ctx.Message.BroadcasterSlug, ctx.Message.ModeratorUsername, ctx.Message.BannedUsername, ctx.Message.IsPermanent);
        return Task.CompletedTask;
    }
}

public class KicksGiftedConsumer(ILogger<KicksGiftedConsumer> log) : IConsumer<KicksGifted>
{
    public Task Consume(ConsumeContext<KicksGifted> ctx)
    {
        log.LogInformation("KICKS [{Channel}] {User} sent {Amount}", ctx.Message.BroadcasterSlug, ctx.Message.GifterUsername, ctx.Message.Amount);
        return Task.CompletedTask;
    }
}

public class ChannelRewardRedemptionUpdatedConsumer(ILogger<ChannelRewardRedemptionUpdatedConsumer> log) : IConsumer<ChannelRewardRedemptionUpdated>
{
    public Task Consume(ConsumeContext<ChannelRewardRedemptionUpdated> ctx)
    {
        log.LogInformation("REWARD [{Channel}] {User} redeemed {Title} status={Status}", ctx.Message.BroadcasterSlug, ctx.Message.Username, ctx.Message.RewardTitle, ctx.Message.Status);
        return Task.CompletedTask;
    }
}

public class KickEventUnknownConsumer(ILogger<KickEventUnknownConsumer> log) : IConsumer<KickEventUnknown>
{
    public Task Consume(ConsumeContext<KickEventUnknown> ctx)
    {
        log.LogWarning("UNKNOWN [{Channel}] type={Type} version={Version} payload={Payload}",
            ctx.Message.BroadcasterSlug, ctx.Message.EventType, ctx.Message.EventVersion, ctx.Message.RawPayload);
        return Task.CompletedTask;
    }
}
