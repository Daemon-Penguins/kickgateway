using MassTransit;
using TailoredApps.KickGateway.Contracts;
using TailoredApps.KickGateway.Contracts.Events;

namespace TailoredApps.KickGateway.Api.LiveFeed;

/// <summary>
/// MassTransit consumer registered on a per-API-process queue. Receives every
/// published contract, looks up the broadcaster's client app, and pushes a
/// <see cref="LiveEvent"/> into the in-memory buffer the admin UI reads from.
///
/// One class implementing 11 IConsumer<T> interfaces — MassTransit binds the
/// shared queue to each contract's exchange so this consumer sees the firehose.
/// </summary>
public sealed class LiveFeedConsumer :
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
    private readonly LiveEventBuffer _buffer;
    private readonly BroadcasterClientResolver _resolver;

    public LiveFeedConsumer(LiveEventBuffer buffer, BroadcasterClientResolver resolver)
    {
        _buffer = buffer;
        _resolver = resolver;
    }

    public Task Consume(ConsumeContext<ChatMessageSent> ctx) =>
        Push(ctx.Message, "chat.message.sent",
             $"{Trim(ctx.Message.SenderUsername, 30)}: {Trim(ctx.Message.Content, 120)}");

    public Task Consume(ConsumeContext<ChannelFollowed> ctx) =>
        Push(ctx.Message, "channel.followed",
             $"{Trim(ctx.Message.FollowerUsername, 30)} followed");

    public Task Consume(ConsumeContext<ChannelSubscriptionNew> ctx) =>
        Push(ctx.Message, "channel.subscription.new",
             $"{Trim(ctx.Message.SubscriberUsername, 30)} subscribed ({ctx.Message.Duration}mo)");

    public Task Consume(ConsumeContext<ChannelSubscriptionGifts> ctx) =>
        Push(ctx.Message, "channel.subscription.gifts",
             $"{Trim(ctx.Message.GifterUsername, 30)} gifted {ctx.Message.GiftCount}× tier {ctx.Message.Tier}");

    public Task Consume(ConsumeContext<ChannelSubscriptionRenewal> ctx) =>
        Push(ctx.Message, "channel.subscription.renewal",
             $"{Trim(ctx.Message.SubscriberUsername, 30)} renewed ({ctx.Message.CumulativeMonths}mo total)");

    public Task Consume(ConsumeContext<LivestreamStatusUpdated> ctx) =>
        Push(ctx.Message, "livestream.status.updated",
             ctx.Message.IsLive ? $"went live — {Trim(ctx.Message.Title, 80)}" : "went offline");

    public Task Consume(ConsumeContext<LivestreamMetadataUpdated> ctx) =>
        Push(ctx.Message, "livestream.metadata.updated",
             $"title/category changed — {Trim(ctx.Message.Title, 60)} · {Trim(ctx.Message.CategoryName, 40)}");

    public Task Consume(ConsumeContext<ModerationBanned> ctx) =>
        Push(ctx.Message, "moderation.banned",
             $"{Trim(ctx.Message.ModeratorUsername, 25)} → {Trim(ctx.Message.BannedUsername, 25)}{(ctx.Message.IsPermanent ? " (perma)" : "")}");

    public Task Consume(ConsumeContext<KicksGifted> ctx) =>
        Push(ctx.Message, "kicks.gifted",
             $"{Trim(ctx.Message.GifterUsername, 30)} sent {ctx.Message.Amount} kicks");

    public Task Consume(ConsumeContext<ChannelRewardRedemptionUpdated> ctx) =>
        Push(ctx.Message, "channel.reward.redemption.updated",
             $"{Trim(ctx.Message.Username, 30)} redeemed {Trim(ctx.Message.RewardTitle, 40)} [{ctx.Message.Status}]");

    public Task Consume(ConsumeContext<KickEventUnknown> ctx) =>
        Push(ctx.Message, ctx.Message.EventType, $"(unknown event — raw payload available)");

    private async Task Push(IKickEvent ev, string eventType, string summary)
    {
        var clientId = await _resolver.GetClientAppIdAsync(ev.BroadcasterAccountId);
        if (clientId is null) return;            // broadcaster missing from DB — nothing to attribute
        _buffer.Append(new LiveEvent(
            ClientAppId: clientId.Value,
            BroadcasterAccountId: ev.BroadcasterAccountId,
            BroadcasterSlug: ev.BroadcasterSlug,
            BroadcasterUserId: ev.BroadcasterUserId,
            EventType: eventType,
            Summary: summary,
            ReceivedAt: ev.ReceivedAt,
            KickTimestamp: ev.KickTimestamp,
            KickMessageId: ev.KickMessageId,
            RawPayload: ev.RawPayload));
    }

    private static string Trim(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }
}
