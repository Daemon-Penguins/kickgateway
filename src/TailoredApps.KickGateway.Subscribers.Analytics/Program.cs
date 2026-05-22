using MassTransit;
using TailoredApps.KickGateway.Contracts;
using TailoredApps.KickGateway.Contracts.Events;
using TailoredApps.KickGateway.Subscribers.Analytics.Consumers;

// Subscriber app #3 — "analytics". One consumer class implements IConsumer<T>
// for every event type. Each consumer interface gets its own receive endpoint
// + queue and is filtered by the same Subscribers:Channels list.

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

var channels = builder.Configuration.GetSection("Subscribers:Channels").Get<string[]>() ?? Array.Empty<string>();

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<AnalyticsAllEventsConsumer>();

    x.UsingRabbitMq((ctx, cfg) =>
    {
        var rmq = builder.Configuration.GetSection("RabbitMq");
        cfg.Host(rmq["Host"] ?? "localhost", ushort.Parse(rmq["Port"] ?? "5672"), rmq["VirtualHost"] ?? "/", h =>
        {
            h.Username(rmq["Username"] ?? "guest");
            h.Password(rmq["Password"] ?? "guest");
        });

        KickEventTopology.ConfigurePublishTopology(cfg);

        // One endpoint per event type so each binding can carry its own
        // channel filter. The same AnalyticsAllEventsConsumer is configured
        // on every endpoint — MassTransit picks the IConsumer<TEvent>
        // interface that matches the bound exchange.
        BindEvent<ChatMessageSent>("chat-message-sent");
        BindEvent<ChannelFollowed>("channel-followed");
        BindEvent<ChannelSubscriptionNew>("channel-subscription-new");
        BindEvent<ChannelSubscriptionGifts>("channel-subscription-gifts");
        BindEvent<ChannelSubscriptionRenewal>("channel-subscription-renewal");
        BindEvent<LivestreamStatusUpdated>("livestream-status-updated");
        BindEvent<LivestreamMetadataUpdated>("livestream-metadata-updated");
        BindEvent<ModerationBanned>("moderation-banned");
        BindEvent<KicksGifted>("kicks-gifted");
        BindEvent<ChannelRewardRedemptionUpdated>("channel-reward-redemption-updated");
        BindEvent<KickEventUnknown>("kick-event-unknown");

        void BindEvent<TEvent>(string suffix) where TEvent : class
        {
            cfg.ReceiveEndpoint($"analytics-{suffix}", e =>
            {
                KickEventTopology.BindKickEvent<TEvent>(e, channels);
                e.ConfigureConsumer<AnalyticsAllEventsConsumer>(ctx);
            });
        }
    });
});

builder.Services.AddHostedService(sp => new HeartbeatService(
    sp.GetRequiredService<ILogger<HeartbeatService>>(), "analytics", channels));

var host = builder.Build();
host.Run();

internal sealed class HeartbeatService(ILogger<HeartbeatService> log, string app, string[] channels) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var filter = channels.Length == 0 ? "ALL CHANNELS" : string.Join(",", channels);
        log.LogInformation("[{App}] online — durable queues bound with routing keys: {Filter}", app, filter);
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
