using MassTransit;
using TailoredApps.KickGateway.Contracts;
using TailoredApps.KickGateway.Contracts.Events;
using TailoredApps.KickGateway.Subscribers.Loyalty.Consumers;

// Subscriber app #1 — "loyalty".
//
// Reads a "Subscribers:Channels" list from config — e.g. ["xqc","adin"] —
// and binds its durable queues ONLY to those routing keys. RabbitMQ filters
// at the broker; messages for other channels never touch this process.
// If the list is empty (or contains "#"), the subscriber falls back to
// the all-channels firehose.

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

var channels = builder.Configuration.GetSection("Subscribers:Channels").Get<string[]>() ?? Array.Empty<string>();

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<LoyaltyChatConsumer>();
    x.AddConsumer<LoyaltySubscriptionConsumer>();
    x.AddConsumer<LoyaltyKicksGiftedConsumer>();

    x.UsingRabbitMq((ctx, cfg) =>
    {
        var rmq = builder.Configuration.GetSection("RabbitMq");
        cfg.Host(rmq["Host"] ?? "localhost", ushort.Parse(rmq["Port"] ?? "5672"), rmq["VirtualHost"] ?? "/", h =>
        {
            h.Username(rmq["Username"] ?? "guest");
            h.Password(rmq["Password"] ?? "guest");
        });

        KickEventTopology.ConfigurePublishTopology(cfg);

        var fmt = new KebabCaseEndpointNameFormatter(prefix: "loyalty", includeNamespace: false);

        cfg.ReceiveEndpoint(fmt.Consumer<LoyaltyChatConsumer>(), e =>
        {
            KickEventTopology.BindKickEvent<ChatMessageSent>(e, channels);
            e.ConfigureConsumer<LoyaltyChatConsumer>(ctx);
        });
        cfg.ReceiveEndpoint(fmt.Consumer<LoyaltySubscriptionConsumer>(), e =>
        {
            KickEventTopology.BindKickEvent<ChannelSubscriptionNew>(e, channels);
            e.ConfigureConsumer<LoyaltySubscriptionConsumer>(ctx);
        });
        cfg.ReceiveEndpoint(fmt.Consumer<LoyaltyKicksGiftedConsumer>(), e =>
        {
            KickEventTopology.BindKickEvent<KicksGifted>(e, channels);
            e.ConfigureConsumer<LoyaltyKicksGiftedConsumer>(ctx);
        });
    });
});

builder.Services.AddHostedService(sp => new HeartbeatService(
    sp.GetRequiredService<ILogger<HeartbeatService>>(), "loyalty", channels));

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
