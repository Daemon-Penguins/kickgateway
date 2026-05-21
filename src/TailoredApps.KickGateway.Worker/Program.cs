using MassTransit;
using TailoredApps.KickGateway.Worker.Consumers;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<ChatMessageSentConsumer>();
    x.AddConsumer<ChannelFollowedConsumer>();
    x.AddConsumer<ChannelSubscriptionNewConsumer>();
    x.AddConsumer<ChannelSubscriptionGiftsConsumer>();
    x.AddConsumer<ChannelSubscriptionRenewalConsumer>();
    x.AddConsumer<LivestreamStatusUpdatedConsumer>();
    x.AddConsumer<LivestreamMetadataUpdatedConsumer>();
    x.AddConsumer<ModerationBannedConsumer>();
    x.AddConsumer<KicksGiftedConsumer>();
    x.AddConsumer<ChannelRewardRedemptionUpdatedConsumer>();
    x.AddConsumer<KickEventUnknownConsumer>();

    x.SetKebabCaseEndpointNameFormatter();

    x.UsingRabbitMq((ctx, cfg) =>
    {
        var rmq = builder.Configuration.GetSection("RabbitMq");
        cfg.Host(rmq["Host"] ?? "localhost", ushort.Parse(rmq["Port"] ?? "5672"), rmq["VirtualHost"] ?? "/", h =>
        {
            h.Username(rmq["Username"] ?? "guest");
            h.Password(rmq["Password"] ?? "guest");
        });
        cfg.ConfigureEndpoints(ctx);
    });
});

var host = builder.Build();
host.Run();
