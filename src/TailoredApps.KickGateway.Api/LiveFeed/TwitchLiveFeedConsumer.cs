using MassTransit;
using TailoredApps.KickGateway.Api.Data;
using TailoredApps.KickGateway.Telemetry;
using TailoredApps.TwitchGateway.Contracts;
using TailoredApps.TwitchGateway.Contracts.Channel;
using TailoredApps.TwitchGateway.Contracts.Chat;

namespace TailoredApps.KickGateway.Api.LiveFeed;

/// <summary>
/// The Api's own tap on the Twitch exchanges (auto-delete, per-replica queue): measures the end-to-end
/// delivery lag of every Twitch contract (<c>twitchgateway.event.delivery_lag</c>) exactly as a downstream
/// subscriber would see it, and feeds chat messages to the public landing page's <see cref="HomeFeed"/>.
/// The admin live feed stays Kick-only for now.
/// </summary>
public sealed class TwitchLiveFeedConsumer :
    IConsumer<TwitchChatMessage>,
    IConsumer<TwitchChatNotification>,
    IConsumer<TwitchChatMessageDeleted>,
    IConsumer<TwitchChatCleared>,
    IConsumer<TwitchChatUserMessagesCleared>,
    IConsumer<TwitchChatSettingsUpdated>,
    IConsumer<TwitchStreamOnline>,
    IConsumer<TwitchStreamOffline>,
    IConsumer<TwitchChannelUpdated>,
    IConsumer<TwitchEventUnknown>
{
    private readonly HomeFeed _home;

    public TwitchLiveFeedConsumer(HomeFeed home) { _home = home; }

    public Task Consume(ConsumeContext<TwitchChatMessage> c)
    {
        var m = c.Message;
        _home.Append(new HomeFeedEntry(
            HomeFeedKind.Chat,
            ChatPlatforms.Twitch,
            m.BroadcasterLogin,
            m.ChatterName.Length > 0 ? m.ChatterName : m.ChatterLogin,
            ChatBadgeLabels.FromTwitch(m.Badges),
            m.Text,
            m.Color,
            HomeFeed.ChatKey(ChatPlatforms.Twitch, m.ChatMessageId)));
        return Record(m);
    }

    public Task Consume(ConsumeContext<TwitchChatNotification> c) => Record(c.Message);
    public Task Consume(ConsumeContext<TwitchChatMessageDeleted> c) => Record(c.Message);
    public Task Consume(ConsumeContext<TwitchChatCleared> c) => Record(c.Message);
    public Task Consume(ConsumeContext<TwitchChatUserMessagesCleared> c) => Record(c.Message);
    public Task Consume(ConsumeContext<TwitchChatSettingsUpdated> c) => Record(c.Message);
    public Task Consume(ConsumeContext<TwitchStreamOnline> c) => Record(c.Message);
    public Task Consume(ConsumeContext<TwitchStreamOffline> c) => Record(c.Message);
    public Task Consume(ConsumeContext<TwitchChannelUpdated> c) => Record(c.Message);
    public Task Consume(ConsumeContext<TwitchEventUnknown> c) => Record(c.Message);

    private static Task Record(ITwitchEvent ev)
    {
        TwitchGatewayMetrics.RecordDeliveryLag(ev.EventType, DateTime.UtcNow - ev.ReceivedAt);
        return Task.CompletedTask;
    }
}
