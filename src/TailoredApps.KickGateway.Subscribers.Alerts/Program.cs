using MassTransit;
using TailoredApps.KickGateway.Contracts;
using TailoredApps.KickGateway.Contracts.Events;
using TailoredApps.KickGateway.Subscribers.Alerts.Consumers;

// Subscriber app #2 — "alerts". Same shape as Loyalty, listens to a different
// set of contracts. Channel filter comes from Subscribers:Channels config.

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

var channels = builder.Configuration.GetSection("Subscribers:Channels").Get<string[]>() ?? Array.Empty<string>();

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<AlertsLivestreamStatusConsumer>();
    x.AddConsumer<AlertsModerationBannedConsumer>();
    x.AddConsumer<AlertsFollowedConsumer>();

    x.UsingRabbitMq((ctx, cfg) =>
    {
        var rmq = builder.Configuration.GetSection("RabbitMq");
        cfg.Host(rmq["Host"] ?? "localhost", ushort.Parse(rmq["Port"] ?? "5672"), rmq["VirtualHost"] ?? "/", h =>
        {
            h.Username(rmq["Username"] ?? "guest");
            h.Password(rmq["Password"] ?? "guest");
        });

        KickEventTopology.ConfigurePublishTopology(cfg);

        var fmt = new KebabCaseEndpointNameFormatter(prefix: "alerts", includeNamespace: false);

        cfg.ReceiveEndpoint(fmt.Consumer<AlertsLivestreamStatusConsumer>(), e =>
        {
            KickEventTopology.BindKickEvent<LivestreamStatusUpdated>(e, channels);
            e.ConfigureConsumer<AlertsLivestreamStatusConsumer>(ctx);
        });
        cfg.ReceiveEndpoint(fmt.Consumer<AlertsModerationBannedConsumer>(), e =>
        {
            KickEventTopology.BindKickEvent<ModerationBanned>(e, channels);
            e.ConfigureConsumer<AlertsModerationBannedConsumer>(ctx);
        });
        cfg.ReceiveEndpoint(fmt.Consumer<AlertsFollowedConsumer>(), e =>
        {
            KickEventTopology.BindKickEvent<ChannelFollowed>(e, channels);
            e.ConfigureConsumer<AlertsFollowedConsumer>(ctx);
        });
    });
});

builder.Services.AddHostedService(sp => new HeartbeatService(
    sp.GetRequiredService<ILogger<HeartbeatService>>(), "alerts", channels));

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
