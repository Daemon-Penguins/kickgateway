using MassTransit;
using TailoredApps.KickGateway.Api.Data;
using TailoredApps.KickGateway.Contracts.Realtime;

namespace TailoredApps.KickGateway.Api.LiveFeed;

/// <summary>
/// The Api's tap on the Kick <b>realtime</b> (Pusher) chat exchange, per replica (auto-delete queue): feeds the
/// landing page's <see cref="HomeFeed"/> with chat from channels followed by the realtime listener only. A channel
/// followed on both transports delivers the same message here and to <see cref="LiveFeedConsumer"/>; the feed
/// collapses them on Kick's message id. Nothing else happens here - the realtime family has its own ingest
/// metrics in the listener, and the admin live feed stays webhook-only.
/// </summary>
public sealed class RealtimeLiveFeedConsumer : IConsumer<RealtimeChatMessage>
{
    private readonly HomeFeed _home;

    public RealtimeLiveFeedConsumer(HomeFeed home) { _home = home; }

    public Task Consume(ConsumeContext<RealtimeChatMessage> ctx)
    {
        var m = ctx.Message;
        _home.Append(new HomeFeedEntry(
            HomeFeedKind.Chat,
            ChatPlatforms.Kick,
            m.BroadcasterSlug,
            m.SenderUsername,
            ChatBadgeLabels.FromKick(m.SenderBadges),
            m.Content,
            m.SenderColor,
            HomeFeed.ChatKey(ChatPlatforms.Kick, m.MessageId)));
        return Task.CompletedTask;
    }
}
