using MassTransit;
using TailoredApps.KickGateway.Contracts;
using TailoredApps.KickGateway.Contracts.Events;

namespace TailoredApps.KickGateway.Subscribers.Analytics.Consumers;

// This subscriber registers ONE consumer per concrete event type and routes
// them through a shared IKickEvent counter, demonstrating how a subscriber can
// fan in across all gateway events with a single handler implementation.
public sealed class AnalyticsAllEventsConsumer(ILogger<AnalyticsAllEventsConsumer> log) :
    IConsumer<ChatMessageSent>,
    IConsumer<ChannelFollowed>,
    IConsumer<ChannelSubscriptionNew>,
    IConsumer<ChannelSubscriptionGifts>,
    IConsumer<ChannelSubscriptionRenewal>,
    IConsumer<LivestreamStatusUpdated>,
    IConsumer<LivestreamMetadataUpdated>,
    IConsumer<ModerationBanned>,
    IConsumer<KicksGifted>,
    IConsumer<ChannelRewardRedemptionUpdated>,
    IConsumer<KickEventUnknown>
{
    public Task Consume(ConsumeContext<ChatMessageSent> ctx) => Count(ctx.Message, "chat");
    public Task Consume(ConsumeContext<ChannelFollowed> ctx) => Count(ctx.Message, "follow");
    public Task Consume(ConsumeContext<ChannelSubscriptionNew> ctx) => Count(ctx.Message, "sub-new");
    public Task Consume(ConsumeContext<ChannelSubscriptionGifts> ctx) => Count(ctx.Message, "sub-gift");
    public Task Consume(ConsumeContext<ChannelSubscriptionRenewal> ctx) => Count(ctx.Message, "sub-renew");
    public Task Consume(ConsumeContext<LivestreamStatusUpdated> ctx) => Count(ctx.Message, "stream-status");
    public Task Consume(ConsumeContext<LivestreamMetadataUpdated> ctx) => Count(ctx.Message, "stream-meta");
    public Task Consume(ConsumeContext<ModerationBanned> ctx) => Count(ctx.Message, "ban");
    public Task Consume(ConsumeContext<KicksGifted> ctx) => Count(ctx.Message, "kicks");
    public Task Consume(ConsumeContext<ChannelRewardRedemptionUpdated> ctx) => Count(ctx.Message, "reward");
    public Task Consume(ConsumeContext<KickEventUnknown> ctx) => Count(ctx.Message, $"unknown:{ctx.Message.EventType}");

    private Task Count(IKickEvent evt, string kind)
    {
        log.LogInformation("[analytics] {Kind} on {Channel} at {When:O}", kind, evt.BroadcasterSlug, evt.ReceivedAt);
        return Task.CompletedTask;
    }
}
