using MassTransit;
using TailoredApps.KickGateway.Telemetry;
using TailoredApps.TwitchGateway.Contracts;
using TailoredApps.TwitchGateway.Contracts.Channel;
using TailoredApps.TwitchGateway.Contracts.Chat;

namespace TailoredApps.KickGateway.Api.LiveFeed;

/// <summary>
/// The Api's own tap on the Twitch exchanges (auto-delete, per-replica queue): measures the end-to-end
/// delivery lag of every Twitch contract (<c>twitchgateway.event.delivery_lag</c>) exactly as a downstream
/// subscriber would see it. The admin live feed stays Kick-only for now; this consumer only records the metric.
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
    public Task Consume(ConsumeContext<TwitchChatMessage> c) => Record(c.Message);
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
