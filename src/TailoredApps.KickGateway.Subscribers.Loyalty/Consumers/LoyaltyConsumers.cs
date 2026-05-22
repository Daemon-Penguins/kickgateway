using MassTransit;
using TailoredApps.KickGateway.Contracts.Events;

namespace TailoredApps.KickGateway.Subscribers.Loyalty.Consumers;

public sealed class LoyaltyChatConsumer(ILogger<LoyaltyChatConsumer> log) : IConsumer<ChatMessageSent>
{
    public Task Consume(ConsumeContext<ChatMessageSent> ctx)
    {
        log.LogInformation("[loyalty] chat from {User} on {Channel}: {Text}",
            ctx.Message.SenderUsername, ctx.Message.BroadcasterSlug, ctx.Message.Content);
        return Task.CompletedTask;
    }
}

public sealed class LoyaltySubscriptionConsumer(ILogger<LoyaltySubscriptionConsumer> log) : IConsumer<ChannelSubscriptionNew>
{
    public Task Consume(ConsumeContext<ChannelSubscriptionNew> ctx)
    {
        log.LogInformation("[loyalty] +1 sub {User} on {Channel} ({Months}mo)",
            ctx.Message.SubscriberUsername, ctx.Message.BroadcasterSlug, ctx.Message.Duration);
        return Task.CompletedTask;
    }
}

public sealed class LoyaltyKicksGiftedConsumer(ILogger<LoyaltyKicksGiftedConsumer> log) : IConsumer<KicksGifted>
{
    public Task Consume(ConsumeContext<KicksGifted> ctx)
    {
        log.LogInformation("[loyalty] {User} gifted {Amount} kicks on {Channel}",
            ctx.Message.GifterUsername, ctx.Message.Amount, ctx.Message.BroadcasterSlug);
        return Task.CompletedTask;
    }
}
